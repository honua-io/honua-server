# Greenfield Architecture Plan

> Status note: This document mixes current implementation details with target architecture. The current tree already ships core server APIs; admin UI and deployment templates are still pending. See `docs/MVP_PLAN.md` for current status and open gaps.

## Executive Summary

This document outlines the architecture for a greenfield Honua MVP, addressing the coupling and structural issues in the current codebase while preserving the good patterns from the platform layer.

---

## Current State Analysis

### What's Wrong (Domain Layer)

**GeoservicesRESTFeatureServerController** — 22 dependencies, monolithic

```csharp
// Current: 22 constructor parameters
public GeoservicesRESTFeatureServerController(
    IFeatureRepository repository,
    ICatalogService catalog,
    IMetadataRegistry metadataRegistry,
    IGeoservicesQueryParser queryParser,
    IGeoservicesQueryService queryService,
    IGeoservicesEditingService editingService,
    IGeoservicesAttachmentService attachmentService,
    IFeatureAttachmentOrchestrator attachmentOrchestrator,
    IAttachmentStoreSelector attachmentStoreSelector,
    IGeoservicesChangeTracker changeTracker,
    IGeoservicesAuditLogger auditLogger,
    IGeoservicesFeatureServerMetadataEndpoints metadataEndpoints,
    IGeoservicesFeatureServerRelatedRecordsEndpoints relatedRecordsEndpoints,
    IReplicaRegistry replicaRegistry,
    IStreamingKmlWriter streamingKmlWriter,
    ICsvExporter csvExporter,
    IShapefileExporter shapefileExporter,
    ILimitResolver limitResolver,
    IObservabilityService observability,
    IOptions<GeoservicesRESTOptions> restOptions,
    IOptions<TopoJsonLimits> topoJsonLimits,
    ILogger<GeoservicesRESTFeatureServerController> logger)
```

**Problems:**
1. Single Responsibility Principle violation — one class handles query, edit, export, attachments, metadata
2. Open/Closed Principle violation — adding features requires modifying the controller
3. Testing complexity — need to mock 22 dependencies
4. Cognitive load — hard to understand what the class does

### What's Right (Platform Layer)

**FeatureRepository** — 6 dependencies, focused interface

```csharp
public interface IFeatureRepository
{
    Task<FeatureRecord?> GetAsync(...);
    Task<IReadOnlyList<FeatureRecord>> QueryAsync(...);
    Task<long> CountAsync(...);
    Task<FeatureRecord> CreateAsync(...);
    Task<FeatureRecord> UpdateAsync(...);
    Task<bool> DeleteAsync(...);
    Task<IReadOnlyList<DistinctResult>> QueryDistinctAsync(...);
    Task<Envelope?> QueryExtentAsync(...);
    Task<IReadOnlyList<StatisticResult>> QueryStatisticsAsync(...);
}
```

**PostgresDataStoreProvider** — Composition over inheritance

```
PostgresDataStoreProvider
├── PostgresConnectionManager      (connection lifecycle)
├── PostgresFeatureOperations      (CRUD operations)
├── PostgresBulkOperations         (batch operations)
├── PostgresVectorTileGenerator    (MVT generation)
├── PostgresFeatureQueryBuilder    (SQL construction)
└── QueryBuilderPool               (object pooling)
```

**Pattern to adopt:** Small, focused classes composed together.

---

## Greenfield Architecture

### Design Principles

| Principle | Application |
|-----------|-------------|
| **Vertical Slices** | Organize by feature (Query, Edit, Attachments), not layer |
| **Composition** | Small classes composed together, no deep inheritance |
| **Interface Segregation** | Narrow interfaces, one responsibility each |
| **Dependency Inversion** | Depend on abstractions, inject implementations |
| **CQRS-lite** | Separate query and command paths (not full event sourcing) |

### Project Structure

```
Honua/
├── src/
│   ├── Honua.Server/                    # Host + API endpoints
│   │   ├── Program.cs                   # Composition root
│   │   │
│   │   ├── Features/                    # VERTICAL SLICES
│   │   │   ├── Query/                   # Feature query slice
│   │   │   │   ├── QueryEndpoint.cs     # Minimal API endpoint
│   │   │   │   ├── QueryRequest.cs      # Request model
│   │   │   │   ├── QueryResponse.cs     # Response model
│   │   │   │   ├── QueryParser.cs       # Parse HTTP → domain
│   │   │   │   ├── QueryHandler.cs      # Business logic
│   │   │   │   └── QueryValidator.cs    # Input validation
│   │   │   │
│   │   │   ├── Edit/                    # Feature editing slice
│   │   │   │   ├── EditEndpoint.cs
│   │   │   │   ├── ApplyEditsRequest.cs
│   │   │   │   ├── ApplyEditsResponse.cs
│   │   │   │   ├── EditParser.cs
│   │   │   │   ├── EditHandler.cs
│   │   │   │   └── EditValidator.cs
│   │   │   │
│   │   │   ├── Attachments/             # Attachment operations slice
│   │   │   │   ├── AttachmentEndpoints.cs
│   │   │   │   ├── AttachmentHandler.cs
│   │   │   │   └── AttachmentValidator.cs
│   │   │   │
│   │   │   ├── Metadata/                # Service/layer metadata slice
│   │   │   │   ├── MetadataEndpoint.cs
│   │   │   │   └── MetadataBuilder.cs
│   │   │   │
│   │   │   ├── VectorTiles/             # MVT tile generation slice
│   │   │   │   ├── VectorTileEndpoint.cs
│   │   │   │   ├── VectorTileHandler.cs
│   │   │   │   └── TileJsonBuilder.cs
│   │   │   │
│   │   │   ├── OgcFeatures/             # OGC API Features slice
│   │   │   │   ├── CollectionsEndpoint.cs
│   │   │   │   ├── ItemsEndpoint.cs
│   │   │   │   └── OgcFeaturesHandler.cs
│   │   │   │
│   │   │   └── Health/                  # Health checks slice
│   │   │       └── HealthEndpoint.cs
│   │   │
│   │   ├── Infrastructure/              # Cross-cutting concerns
│   │   │   ├── Auth/
│   │   │   ├── Observability/
│   │   │   ├── ErrorHandling/
│   │   │   └── Serialization/
│   │   │
│   │   └── Configuration/               # App configuration
│   │
│   ├── Honua.Core/                      # Domain logic (no HTTP deps)
│   │   ├── Features/
│   │   │   ├── FeatureRecord.cs         # Core domain model
│   │   │   ├── FeatureQuery.cs          # Query specification
│   │   │   └── FeatureEdit.cs           # Edit command
│   │   │
│   │   ├── Layers/
│   │   │   ├── LayerDefinition.cs
│   │   │   └── FieldDefinition.cs
│   │   │
│   │   ├── Geometry/
│   │   │   ├── Envelope.cs
│   │   │   └── SpatialFilter.cs
│   │   │
│   │   └── Abstractions/
│   │       ├── IFeatureStore.cs         # Data access abstraction
│   │       └── ILayerCatalog.cs         # Layer metadata abstraction
│   │
│   ├── Honua.Postgres/                  # PostgreSQL implementation
│   │   ├── PostgresFeatureStore.cs      # IFeatureStore implementation
│   │   ├── PostgresQueryBuilder.cs      # SQL query construction
│   │   ├── PostgresConnectionPool.cs    # Connection management
│   │   └── ServiceCollectionExtensions.cs
│   │
│   └── Honua.Admin/                     # Blazor WASM admin UI
│       └── ...
│
├── tests/
│   ├── Honua.Server.Tests/
│   │   ├── Features/
│   │   │   ├── Query/
│   │   │   │   ├── QueryEndpointTests.cs      # Integration
│   │   │   │   ├── QueryParserTests.cs        # Unit
│   │   │   │   └── QueryHandlerTests.cs       # Unit
│   │   │   ├── Edit/
│   │   │   └── Attachments/
│   │   └── Fixtures/
│   │       └── PostgresFixture.cs             # Testcontainers
│   │
│   └── Honua.Core.Tests/
│       └── ...
│
└── docker/
    └── ...
```

### Vertical Slice Pattern

Each slice is **self-contained** and follows the same structure:

```
Feature Slice
├── Endpoint      → HTTP handling (routing, auth, response formatting)
├── Request       → Strongly-typed input model
├── Response      → Strongly-typed output model
├── Parser        → Transform HTTP request → domain objects
├── Handler       → Business logic orchestration
└── Validator     → Input validation rules
```

**Benefits:**
- Add features by adding folders, not modifying existing code
- Each slice testable in isolation
- Easy to understand — open folder, see everything related
- Low coupling between slices

---

## Fixing High Coupling

### Before: Controller with 22 Dependencies

```
┌─────────────────────────────────────────────────────────────┐
│         GeoservicesRESTFeatureServerController              │
│                                                             │
│  Knows about: Query, Edit, Export, Attachments, Metadata,   │
│  Related Records, Replicas, KML, CSV, Shapefile, Limits,    │
│  Caching, Auditing, Change Tracking, ...                    │
│                                                             │
│  22 injected dependencies                                   │
│  7 partial class files                                      │
│  ~3000 lines of code                                        │
└─────────────────────────────────────────────────────────────┘
```

### After: Focused Endpoint Classes

```
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│ QueryEndpoint│  │ EditEndpoint │  │ Attachment   │
│              │  │              │  │ Endpoints    │
│  3 deps      │  │  4 deps      │  │  3 deps      │
│  ~150 lines  │  │  ~200 lines  │  │  ~200 lines  │
└──────────────┘  └──────────────┘  └──────────────┘
        │                │                │
        ▼                ▼                ▼
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│ QueryHandler │  │ EditHandler  │  │ Attachment   │
│              │  │              │  │ Handler      │
│  2 deps      │  │  3 deps      │  │  2 deps      │
└──────────────┘  └──────────────┘  └──────────────┘
        │                │                │
        └────────────────┼────────────────┘
                         ▼
              ┌──────────────────┐
              │  IFeatureStore   │
              │  (single impl)   │
              └──────────────────┘
```

### Dependency Limits

| Component Type | Max Dependencies | Rationale |
|----------------|------------------|-----------|
| Endpoint | 3-5 | Handler, validator, logger, maybe auth |
| Handler | 2-4 | Store, catalog, maybe events |
| Parser | 1-2 | Options only |
| Validator | 1-2 | Options, maybe catalog |

**Rule:** If a class needs more than 5 dependencies, it's doing too much. Split it.

---

## Core Abstractions

### IFeatureStore (Data Access)

```csharp
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

**Note:** Single interface for MVP. If CQRS separation needed later, split into `IFeatureReader` and `IFeatureWriter`.

### ILayerCatalog (Metadata)

```csharp
public interface ILayerCatalog
{
    Task<LayerDefinition?> GetLayerAsync(string serviceId, int layerIndex, CancellationToken ct);
    Task<IReadOnlyList<LayerDefinition>> ListLayersAsync(string serviceId, CancellationToken ct);
    Task<ServiceDefinition?> GetServiceAsync(string serviceId, CancellationToken ct);
}
```

### IAttachmentStore (Attachments)

```csharp
public interface IAttachmentStore
{
    Task<IReadOnlyList<AttachmentInfo>> ListAsync(string layerId, string featureId, CancellationToken ct);
    Task<Stream> DownloadAsync(string attachmentId, CancellationToken ct);
    Task<AttachmentInfo> UploadAsync(string layerId, string featureId, AttachmentUpload upload, CancellationToken ct);
    Task<bool> DeleteAsync(string attachmentId, CancellationToken ct);
}
```

---

## Endpoint Design

### Minimal API Pattern

```csharp
// Features/Query/QueryEndpoint.cs
public static class QueryEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/rest/services/{serviceId}/FeatureServer/{layerIndex}")
            .WithTags("FeatureServer");

        group.MapGet("/query", HandleQuery)
            .WithName("QueryFeatures")
            .Produces<QueryResponse>(200)
            .ProducesProblem(400)
            .ProducesProblem(404);

        group.MapPost("/query", HandleQuery);  // Same handler for POST
    }

    private static async Task<IResult> HandleQuery(
        string serviceId,
        int layerIndex,
        [AsParameters] QueryRequest request,
        [FromServices] QueryHandler handler,
        [FromServices] ILogger<QueryEndpoint> logger,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(serviceId, layerIndex, request, ct);

        return result.Match(
            success => Results.Ok(success),
            notFound => Results.NotFound(),
            error => Results.Problem(error.Message, statusCode: 400)
        );
    }
}
```

### Handler Pattern

```csharp
// Features/Query/QueryHandler.cs
public sealed class QueryHandler
{
    private readonly IFeatureStore _store;
    private readonly ILayerCatalog _catalog;

    public QueryHandler(IFeatureStore store, ILayerCatalog catalog)
    {
        _store = store;
        _catalog = catalog;
    }

    public async Task<Result<QueryResponse, QueryError>> HandleAsync(
        string serviceId,
        int layerIndex,
        QueryRequest request,
        CancellationToken ct)
    {
        // 1. Resolve layer
        var layer = await _catalog.GetLayerAsync(serviceId, layerIndex, ct);
        if (layer is null)
            return QueryError.LayerNotFound;

        // 2. Parse and validate query
        var query = QueryParser.Parse(request, layer);
        var validation = QueryValidator.Validate(query, layer);
        if (!validation.IsValid)
            return QueryError.InvalidQuery(validation.Errors);

        // 3. Execute query
        var result = await _store.QueryAsync(layer.Id, query, ct);

        // 4. Build response
        return QueryResponseBuilder.Build(result, layer, request.Format);
    }
}
```

**Note:** Handler has 2 dependencies. Parser and Validator are static or injected as needed.

---

## Request/Response Models

### Strongly Typed Requests

```csharp
// Features/Query/QueryRequest.cs
public sealed record QueryRequest
{
    [FromQuery(Name = "where")]
    public string? Where { get; init; }

    [FromQuery(Name = "geometry")]
    public string? Geometry { get; init; }

    [FromQuery(Name = "geometryType")]
    public string? GeometryType { get; init; }

    [FromQuery(Name = "spatialRel")]
    public string? SpatialRel { get; init; }

    [FromQuery(Name = "outFields")]
    public string? OutFields { get; init; }

    [FromQuery(Name = "returnGeometry")]
    public bool ReturnGeometry { get; init; } = true;

    [FromQuery(Name = "returnCountOnly")]
    public bool ReturnCountOnly { get; init; }

    [FromQuery(Name = "resultOffset")]
    public int? ResultOffset { get; init; }

    [FromQuery(Name = "resultRecordCount")]
    public int? ResultRecordCount { get; init; }

    [FromQuery(Name = "f")]
    public string Format { get; init; } = "json";
}
```

### Response with Discriminated Union

```csharp
// Use OneOf or custom Result type
public abstract record QueryResponse;

public sealed record FeatureSetResponse : QueryResponse
{
    public required string ObjectIdFieldName { get; init; }
    public required IReadOnlyList<FieldInfo> Fields { get; init; }
    public required IReadOnlyList<Feature> Features { get; init; }
    public bool ExceededTransferLimit { get; init; }
}

public sealed record CountResponse : QueryResponse
{
    public required long Count { get; init; }
}

public sealed record IdsResponse : QueryResponse
{
    public required string ObjectIdFieldName { get; init; }
    public required IReadOnlyList<long> ObjectIds { get; init; }
}
```

---

## Service Catalog & Metadata

Both protocols require metadata endpoints for service discovery.

### GeoServices REST Catalog

```csharp
// Features/Metadata/CatalogEndpoint.cs
public static class CatalogEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Service catalog (list all services)
        app.MapGet("/rest/services", HandleCatalog);

        // Folder listing (if using folders)
        app.MapGet("/rest/services/{folder}", HandleFolder);

        // Service metadata
        app.MapGet("/rest/services/{serviceId}/FeatureServer", HandleServiceMetadata);

        // Layer metadata
        app.MapGet("/rest/services/{serviceId}/FeatureServer/{layerIndex:int}", HandleLayerMetadata);
    }

    private static async Task<IResult> HandleCatalog(
        ILayerCatalog catalog, CancellationToken ct)
    {
        var services = await catalog.GetAllServicesAsync(ct);

        return Results.Ok(new
        {
            currentVersion = 11.2,
            services = services.Select(s => new
            {
                name = s.Name,
                type = "FeatureServer"
            }),
            folders = Array.Empty<string>()
        });
    }

    private static async Task<IResult> HandleServiceMetadata(
        string serviceId, ILayerCatalog catalog, CancellationToken ct)
    {
        var service = await catalog.GetServiceAsync(serviceId, ct);
        if (service is null) return Results.NotFound();

        var layers = await catalog.GetLayersAsync(serviceId, ct);

        return Results.Ok(new
        {
            currentVersion = 11.2,
            serviceDescription = service.Description,
            hasVersionedData = false,
            supportsDisconnectedEditing = false,
            supportedQueryFormats = "JSON,geoJSON",
            supportsApplyEditsWithGlobalIds = true,
            layers = layers.Select((l, i) => new
            {
                id = i,
                name = l.Name,
                type = "Feature Layer",
                geometryType = l.GeometryType
            }),
            tables = Array.Empty<object>()
        });
    }

    private static async Task<IResult> HandleLayerMetadata(
        string serviceId, int layerIndex, ILayerCatalog catalog, CancellationToken ct)
    {
        var layer = await catalog.GetLayerAsync(serviceId, layerIndex, ct);
        if (layer is null) return Results.NotFound();

        return Results.Ok(new
        {
            currentVersion = 11.2,
            id = layerIndex,
            name = layer.Name,
            type = "Feature Layer",
            geometryType = layer.GeometryType,
            description = layer.Description,
            objectIdField = layer.ObjectIdField,
            globalIdField = layer.GlobalIdField,
            fields = layer.Fields.Select(f => new
            {
                name = f.Name,
                type = f.GeoServicesType,
                alias = f.Alias,
                nullable = f.IsNullable,
                editable = f.IsEditable,
                length = f.Length
            }),
            extent = new
            {
                xmin = layer.Extent.XMin,
                ymin = layer.Extent.YMin,
                xmax = layer.Extent.XMax,
                ymax = layer.Extent.YMax,
                spatialReference = new { wkid = layer.Srid }
            },
            capabilities = "Query,Editing,Create,Update,Delete",
            supportsApplyEditsWithGlobalIds = true
        });
    }
}
```

### OGC API Features Metadata

OGC has built-in metadata through landing page, conformance, and collections:

```csharp
// Features/OgcFeatures/LandingPageHandler.cs
public static IResult HandleLandingPage(HttpRequest request)
{
    var baseUrl = $"{request.Scheme}://{request.Host}/ogc/features";

    return Results.Ok(new
    {
        title = "Honua OGC API Features",
        description = "Access to geospatial data via OGC API Features",
        links = new[]
        {
            new { href = baseUrl, rel = "self", type = "application/json", title = "This document" },
            new { href = $"{baseUrl}/conformance", rel = "conformance", type = "application/json" },
            new { href = $"{baseUrl}/collections", rel = "data", type = "application/json" },
            new { href = $"{baseUrl}/api", rel = "service-desc", type = "application/vnd.oai.openapi+json;version=3.0" }
        }
    });
}

// Features/OgcFeatures/CollectionsHandler.cs
public static async Task<IResult> HandleCollections(
    ILayerCatalog catalog, HttpRequest request, CancellationToken ct)
{
    var baseUrl = $"{request.Scheme}://{request.Host}/ogc/features";
    var layers = await catalog.GetAllLayersAsync(ct);

    return Results.Ok(new
    {
        links = new[]
        {
            new { href = $"{baseUrl}/collections", rel = "self", type = "application/json" }
        },
        collections = layers.Select(l => new
        {
            id = l.Name,
            title = l.Name,
            description = l.Description,
            extent = new
            {
                spatial = new
                {
                    bbox = new[] { new[] { l.Extent.XMin, l.Extent.YMin, l.Extent.XMax, l.Extent.YMax } },
                    crs = "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
                }
            },
            links = new[]
            {
                new { href = $"{baseUrl}/collections/{l.Name}", rel = "self", type = "application/json" },
                new { href = $"{baseUrl}/collections/{l.Name}/items", rel = "items", type = "application/geo+json" }
            }
        })
    });
}
```

### ILayerCatalog Interface (Extended)

```csharp
// Core/Abstractions/ILayerCatalog.cs
public interface ILayerCatalog
{
    // Service discovery
    Task<IReadOnlyList<ServiceDefinition>> GetAllServicesAsync(CancellationToken ct);
    Task<ServiceDefinition?> GetServiceAsync(string serviceId, CancellationToken ct);

    // Layer discovery
    Task<IReadOnlyList<LayerDefinition>> GetAllLayersAsync(CancellationToken ct);
    Task<IReadOnlyList<LayerDefinition>> GetLayersAsync(string serviceId, CancellationToken ct);
    Task<LayerDefinition?> GetLayerAsync(string serviceId, int layerIndex, CancellationToken ct);

    // Cache management
    Task InvalidateCacheAsync(string serviceId, CancellationToken ct);
}
```

### Metadata Storage

Metadata is stored in PostgreSQL and cached:

```sql
-- migrations/001_create_metadata_tables.sql
CREATE TABLE services (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE layers (
    id SERIAL PRIMARY KEY,
    service_id TEXT NOT NULL REFERENCES services(id),
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
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(service_id, layer_index)
);

CREATE TABLE layer_fields (
    id SERIAL PRIMARY KEY,
    layer_id INT NOT NULL REFERENCES layers(id),
    name TEXT NOT NULL,
    column_name TEXT NOT NULL,
    field_type TEXT NOT NULL,
    alias TEXT,
    is_nullable BOOLEAN DEFAULT TRUE,
    is_editable BOOLEAN DEFAULT TRUE,
    length INT
);
```

---

## File Import (No GDAL)

MVP supports importing common vector formats without GDAL dependency to stay lightweight (~25MB vs ~100MB+ with GDAL).

### Supported Formats

| Format | Library | Size Impact | Notes |
|--------|---------|-------------|-------|
| **GeoJSON** | `System.Text.Json` | 0 | Native, RFC 7946 compliant |
| **Shapefile** | `NetTopologySuite.IO.ShapeFile` | ~2MB | NTS already needed for geometry ops |
| **GeoPackage** | `Microsoft.Data.Sqlite` + NTS | ~1MB | SQLite-based, OGC standard |
| **CSV/TSV** | Built-in | 0 | Lat/lon columns or WKT column |
| **KML/KMZ** | `System.Xml` / `System.IO.Compression` | 0 | XML-based, KMZ is zipped KML |

### Not Supported (Requires GDAL)

- FileGDB (proprietary format)
- MapInfo TAB
- Raster formats (GeoTIFF, etc.)
- Exotic vector formats

### Import Pipeline

```
┌─────────────┐     ┌──────────────┐     ┌─────────────┐     ┌──────────┐
│ Upload File │ ──▶ │ Format       │ ──▶ │ Schema      │ ──▶ │ Bulk     │
│ (Stream)    │     │ Detection    │     │ Inference   │     │ Insert   │
└─────────────┘     └──────────────┘     └─────────────┘     └──────────┘
                           │                    │
                           ▼                    ▼
                    ┌──────────────┐     ┌─────────────┐
                    │ IFileReader  │     │ Create      │
                    │ (per format) │     │ Table + Idx │
                    └──────────────┘     └─────────────┘
```

### File Reader Abstraction

```csharp
// Features/Import/IFileReader.cs
namespace Honua.Server.Features.Import;

public interface IFileReader
{
    /// <summary>Formats this reader supports (e.g., ".geojson", ".shp")</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>Detect if this reader can handle the file</summary>
    bool CanRead(Stream stream, string fileName);

    /// <summary>Read features as async enumerable for streaming</summary>
    IAsyncEnumerable<ImportFeature> ReadFeaturesAsync(
        Stream stream,
        string fileName,
        ImportOptions options,
        CancellationToken ct);

    /// <summary>Infer schema from first N features</summary>
    Task<InferredSchema> InferSchemaAsync(
        Stream stream,
        string fileName,
        int sampleSize = 100,
        CancellationToken ct = default);
}

public record ImportFeature(
    IReadOnlyDictionary<string, object?> Attributes,
    NetTopologySuite.Geometries.Geometry? Geometry);

public record InferredSchema(
    IReadOnlyList<InferredField> Fields,
    GeometryType? GeometryType,
    int? Srid);

public record InferredField(
    string Name,
    FieldType InferredType,
    bool Nullable,
    int? MaxLength);

public record ImportOptions
{
    public string? GeometryColumn { get; init; }  // For CSV with WKT
    public string? LatColumn { get; init; }       // For CSV with lat/lon
    public string? LonColumn { get; init; }
    public int? SourceSrid { get; init; }         // Override source SRID
    public int TargetSrid { get; init; } = 4326;  // Reproject to this SRID
    public char CsvDelimiter { get; init; } = ',';
    public bool HasHeader { get; init; } = true;
}
```

### GeoJSON Reader

```csharp
// Features/Import/Readers/GeoJsonReader.cs
namespace Honua.Server.Features.Import.Readers;

public sealed class GeoJsonReader : IFileReader
{
    public IReadOnlyList<string> SupportedExtensions => [".geojson", ".json"];

    public bool CanRead(Stream stream, string fileName)
    {
        // Check for GeoJSON magic bytes or extension
        if (SupportedExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Peek at content
        using var reader = new StreamReader(stream, leaveOpen: true);
        var buffer = new char[100];
        reader.Read(buffer, 0, 100);
        stream.Position = 0;

        var preview = new string(buffer);
        return preview.Contains("\"type\"") &&
               (preview.Contains("FeatureCollection") || preview.Contains("Feature"));
    }

    public async IAsyncEnumerable<ImportFeature> ReadFeaturesAsync(
        Stream stream,
        string fileName,
        ImportOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Stream parse GeoJSON for memory efficiency
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var features = root.GetProperty("type").GetString() == "FeatureCollection"
            ? root.GetProperty("features").EnumerateArray()
            : new[] { root }.AsEnumerable().Select(e => e);

        foreach (var feature in features)
        {
            ct.ThrowIfCancellationRequested();

            var geometry = ParseGeometry(feature.GetProperty("geometry"));
            var properties = ParseProperties(feature.GetProperty("properties"));

            yield return new ImportFeature(properties, geometry);
        }
    }

    private static Geometry? ParseGeometry(JsonElement geom)
    {
        if (geom.ValueKind == JsonValueKind.Null) return null;

        var geoJsonReader = new NetTopologySuite.IO.GeoJsonReader();
        return geoJsonReader.Read<Geometry>(geom.GetRawText());
    }

    private static IReadOnlyDictionary<string, object?> ParseProperties(JsonElement props)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in props.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }
}
```

### Shapefile Reader

```csharp
// Features/Import/Readers/ShapefileReader.cs
namespace Honua.Server.Features.Import.Readers;

public sealed class ShapefileReader : IFileReader
{
    public IReadOnlyList<string> SupportedExtensions => [".shp", ".zip"];

    public async IAsyncEnumerable<ImportFeature> ReadFeaturesAsync(
        Stream stream,
        string fileName,
        ImportOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // If ZIP, extract to temp directory
        var workDir = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? await ExtractZipAsync(stream, ct)
            : Path.GetDirectoryName(fileName)!;

        try
        {
            var shpPath = Directory.GetFiles(workDir, "*.shp").FirstOrDefault()
                ?? throw new ImportException("No .shp file found in archive");

            using var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default);

            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();

                var geometry = reader.Geometry;
                var attributes = new Dictionary<string, object?>();

                for (int i = 1; i < reader.FieldCount; i++) // Skip geometry at 0
                {
                    var name = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    attributes[name] = value;
                }

                yield return new ImportFeature(attributes, geometry);
            }
        }
        finally
        {
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                Directory.Delete(workDir, recursive: true);
        }
    }
}
```

### CSV Reader (Lat/Lon or WKT)

```csharp
// Features/Import/Readers/CsvReader.cs
namespace Honua.Server.Features.Import.Readers;

public sealed class CsvReader : IFileReader
{
    public IReadOnlyList<string> SupportedExtensions => [".csv", ".tsv"];

    public async IAsyncEnumerable<ImportFeature> ReadFeaturesAsync(
        Stream stream,
        string fileName,
        ImportOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var delimiter = options.CsvDelimiter;

        // Read header
        var headerLine = await reader.ReadLineAsync(ct);
        if (headerLine is null) yield break;

        var headers = ParseCsvLine(headerLine, delimiter);
        var latIdx = options.LatColumn is not null
            ? Array.FindIndex(headers, h => h.Equals(options.LatColumn, StringComparison.OrdinalIgnoreCase))
            : FindLatColumn(headers);
        var lonIdx = options.LonColumn is not null
            ? Array.FindIndex(headers, h => h.Equals(options.LonColumn, StringComparison.OrdinalIgnoreCase))
            : FindLonColumn(headers);
        var wktIdx = options.GeometryColumn is not null
            ? Array.FindIndex(headers, h => h.Equals(options.GeometryColumn, StringComparison.OrdinalIgnoreCase))
            : FindWktColumn(headers);

        var wktReader = new NetTopologySuite.IO.WKTReader();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();

            var values = ParseCsvLine(line, delimiter);
            var attributes = new Dictionary<string, object?>();

            Geometry? geometry = null;

            for (int i = 0; i < headers.Length && i < values.Length; i++)
            {
                if (i == latIdx || i == lonIdx || i == wktIdx) continue;
                attributes[headers[i]] = InferValue(values[i]);
            }

            // Build geometry from lat/lon or WKT
            if (wktIdx >= 0 && wktIdx < values.Length && !string.IsNullOrWhiteSpace(values[wktIdx]))
            {
                geometry = wktReader.Read(values[wktIdx]);
            }
            else if (latIdx >= 0 && lonIdx >= 0 && latIdx < values.Length && lonIdx < values.Length)
            {
                if (double.TryParse(values[latIdx], out var lat) &&
                    double.TryParse(values[lonIdx], out var lon))
                {
                    geometry = new NetTopologySuite.Geometries.Point(lon, lat) { SRID = 4326 };
                }
            }

            yield return new ImportFeature(attributes, geometry);
        }
    }

    private static int FindLatColumn(string[] headers) =>
        Array.FindIndex(headers, h =>
            h.Equals("lat", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("latitude", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("y", StringComparison.OrdinalIgnoreCase));

    private static int FindLonColumn(string[] headers) =>
        Array.FindIndex(headers, h =>
            h.Equals("lon", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("lng", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("longitude", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("x", StringComparison.OrdinalIgnoreCase));

    private static int FindWktColumn(string[] headers) =>
        Array.FindIndex(headers, h =>
            h.Equals("wkt", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
            h.Equals("geom", StringComparison.OrdinalIgnoreCase));
}
```

### Import Service

```csharp
// Features/Import/ImportService.cs
namespace Honua.Server.Features.Import;

public sealed class ImportService
{
    private readonly IEnumerable<IFileReader> _readers;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ImportService> _logger;

    public ImportService(
        IEnumerable<IFileReader> readers,
        NpgsqlDataSource dataSource,
        ILogger<ImportService> logger)
    {
        _readers = readers;
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<ImportResult> ImportAsync(
        Stream fileStream,
        string fileName,
        string targetTable,
        ImportOptions options,
        CancellationToken ct)
    {
        var reader = _readers.FirstOrDefault(r => r.CanRead(fileStream, fileName))
            ?? throw new ImportException($"No reader found for file: {fileName}");

        fileStream.Position = 0;

        // 1. Infer schema from sample
        var schema = await reader.InferSchemaAsync(fileStream, fileName, 100, ct);
        fileStream.Position = 0;

        // 2. Create table if needed
        await CreateTableAsync(targetTable, schema, ct);

        // 3. Bulk insert using COPY
        var count = await BulkInsertAsync(
            targetTable,
            schema,
            reader.ReadFeaturesAsync(fileStream, fileName, options, ct),
            ct);

        // 4. Create spatial index
        await CreateSpatialIndexAsync(targetTable, ct);

        return new ImportResult(count, schema.Fields.Count, schema.GeometryType);
    }

    private async Task<long> BulkInsertAsync(
        string table,
        InferredSchema schema,
        IAsyncEnumerable<ImportFeature> features,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var columns = schema.Fields.Select(f => $"\"{f.Name}\"").ToList();
        if (schema.GeometryType.HasValue)
            columns.Add("\"geom\"");

        var copyCommand = $"COPY \"{table}\" ({string.Join(", ", columns)}) FROM STDIN (FORMAT BINARY)";

        await using var writer = await conn.BeginBinaryImportAsync(copyCommand, ct);

        long count = 0;
        await foreach (var feature in features.WithCancellation(ct))
        {
            await writer.StartRowAsync(ct);

            foreach (var field in schema.Fields)
            {
                feature.Attributes.TryGetValue(field.Name, out var value);
                await writer.WriteAsync(value ?? DBNull.Value, ct);
            }

            if (schema.GeometryType.HasValue && feature.Geometry is not null)
            {
                await writer.WriteAsync(feature.Geometry, ct);
            }

            count++;

            if (count % 10000 == 0)
                _logger.LogInformation("Imported {Count} features...", count);
        }

        await writer.CompleteAsync(ct);
        return count;
    }
}

public record ImportResult(long FeatureCount, int FieldCount, GeometryType? GeometryType);
```

### Import Endpoint

```csharp
// Features/Import/ImportEndpoints.cs
namespace Honua.Server.Features.Import;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var import = app.MapGroup("/api/v1/admin/import")
            .WithTags("Import")
            .RequireAuthorization("Admin");

        import.MapPost("/", HandleImport)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data");

        import.MapPost("/preview", HandlePreview)
            .DisableAntiforgery()
            .Accepts<IFormFile>("multipart/form-data");
    }

    private static async Task<IResult> HandleImport(
        IFormFile file,
        [FromQuery] string table,
        [FromQuery] string? latColumn,
        [FromQuery] string? lonColumn,
        [FromQuery] string? geomColumn,
        ImportService importService,
        CancellationToken ct)
    {
        if (file.Length == 0)
            return Results.BadRequest("No file uploaded");

        if (file.Length > 500 * 1024 * 1024) // 500MB limit
            return Results.BadRequest("File too large (max 500MB)");

        var options = new ImportOptions
        {
            LatColumn = latColumn,
            LonColumn = lonColumn,
            GeometryColumn = geomColumn
        };

        await using var stream = file.OpenReadStream();
        var result = await importService.ImportAsync(stream, file.FileName, table, options, ct);

        return Results.Ok(new
        {
            message = $"Imported {result.FeatureCount} features",
            featureCount = result.FeatureCount,
            fieldCount = result.FieldCount,
            geometryType = result.GeometryType?.ToString()
        });
    }

    private static async Task<IResult> HandlePreview(
        IFormFile file,
        ImportService importService,
        CancellationToken ct)
    {
        // Return inferred schema without importing
        await using var stream = file.OpenReadStream();
        var reader = importService.GetReader(file.FileName)
            ?? throw new ImportException($"Unsupported file format: {file.FileName}");

        var schema = await reader.InferSchemaAsync(stream, file.FileName, 10, ct);

        return Results.Ok(new
        {
            fields = schema.Fields.Select(f => new { f.Name, type = f.InferredType.ToString(), f.Nullable }),
            geometryType = schema.GeometryType?.ToString(),
            srid = schema.Srid
        });
    }
}
```

### DI Registration

```csharp
// Program.cs
builder.Services.AddSingleton<IFileReader, GeoJsonReader>();
builder.Services.AddSingleton<IFileReader, ShapefileReader>();
builder.Services.AddSingleton<IFileReader, CsvReader>();
builder.Services.AddSingleton<IFileReader, GeoPackageReader>();
builder.Services.AddSingleton<IFileReader, KmlReader>();
builder.Services.AddScoped<ImportService>();
```

---

## Coordinate Reference Systems (CRS)

MVP supports any EPSG-registered CRS via PostGIS. No GDAL or proj4 library needed — PostGIS handles all transformations.

### Strategy: Let PostGIS Do the Work

| Approach | Pros | Cons |
|----------|------|------|
| **PostGIS ST_Transform** ✅ | Complete EPSG database, handles edge cases, well-tested | Requires database round-trip |
| Proj.NET | Pure .NET | Limited CRS database, maintenance concerns |
| Client-side proj4js | Offloads work | Inconsistent results, client dependency |

**Decision:** Use `ST_Transform` for all reprojection. PostGIS includes the full proj database and handles datum shifts, epoch transformations, and other complexities.

### CRS Detection

```csharp
// Features/Import/CrsDetector.cs
namespace Honua.Server.Features.Import;

public static class CrsDetector
{
    /// <summary>Detect CRS from file metadata or content</summary>
    public static int? DetectSrid(string fileName, Stream? prjStream)
    {
        // 1. Check .prj file (Shapefile companion)
        if (prjStream is not null)
        {
            using var reader = new StreamReader(prjStream);
            var wkt = reader.ReadToEnd();
            return ParseWktSrid(wkt);
        }

        // 2. GeoJSON is always WGS84 per RFC 7946
        if (fileName.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase))
            return 4326;

        // 3. KML is always WGS84 per OGC spec
        if (fileName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
            return 4326;

        // 4. GeoPackage - read from gpkg_spatial_ref_sys table
        // Handled in GeoPackageReader

        return null; // Unknown, require user input
    }

    private static int? ParseWktSrid(string wkt)
    {
        // Look for AUTHORITY["EPSG","4326"] pattern
        var match = Regex.Match(wkt, @"AUTHORITY\[""EPSG"",""(\d+)""\]");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var srid))
            return srid;

        // Common WKT patterns
        if (wkt.Contains("WGS_1984") || wkt.Contains("WGS 84"))
            return 4326;
        if (wkt.Contains("NAD_1983") || wkt.Contains("NAD83"))
            return 4269;

        return null;
    }
}
```

### Storage Strategy

```
┌─────────────────────────────────────────────────────────────────┐
│  CRS Storage Approaches                                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Option A: Store in Native CRS (Recommended for MVP)            │
│  ─────────────────────────────────────────────────────          │
│  • Store geometry as-is from source                              │
│  • Record SRID in layer metadata                                 │
│  • Transform on output when requested                            │
│  • Pro: No data loss, preserves precision                        │
│  • Con: Spatial index less efficient for mixed CRS               │
│                                                                  │
│  Option B: Normalize to WGS84 on Import                         │
│  ─────────────────────────────────────────────────────          │
│  • Transform all data to EPSG:4326 on import                     │
│  • Store original CRS in metadata                                │
│  • Pro: Consistent spatial indexing, simpler queries             │
│  • Con: Potential precision loss for projected data              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

**MVP Decision:** Store in native CRS by default, with option to normalize to 4326. User can choose during import.

### Reprojection Service

```csharp
// Features/Crs/ReprojectionService.cs
namespace Honua.Server.Features.Crs;

public sealed class ReprojectionService
{
    private readonly NpgsqlDataSource _dataSource;

    public ReprojectionService(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    /// <summary>Transform geometry between CRS using PostGIS</summary>
    public async Task<byte[]> TransformAsync(
        byte[] geometryWkb,
        int sourceSrid,
        int targetSrid,
        CancellationToken ct)
    {
        if (sourceSrid == targetSrid)
            return geometryWkb;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT ST_AsBinary(ST_Transform(ST_GeomFromWKB(@geom, @source), @target))",
            conn);

        cmd.Parameters.AddWithValue("geom", geometryWkb);
        cmd.Parameters.AddWithValue("source", sourceSrid);
        cmd.Parameters.AddWithValue("target", targetSrid);

        return (byte[])(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Bulk transform for import pipeline</summary>
    public async IAsyncEnumerable<ImportFeature> TransformFeaturesAsync(
        IAsyncEnumerable<ImportFeature> features,
        int sourceSrid,
        int targetSrid,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (sourceSrid == targetSrid)
        {
            await foreach (var f in features.WithCancellation(ct))
                yield return f;
            yield break;
        }

        // Batch transform for efficiency
        var batch = new List<ImportFeature>();
        const int batchSize = 1000;

        await foreach (var feature in features.WithCancellation(ct))
        {
            batch.Add(feature);
            if (batch.Count >= batchSize)
            {
                foreach (var transformed in await TransformBatchAsync(batch, sourceSrid, targetSrid, ct))
                    yield return transformed;
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            foreach (var transformed in await TransformBatchAsync(batch, sourceSrid, targetSrid, ct))
                yield return transformed;
        }
    }

    private async Task<IReadOnlyList<ImportFeature>> TransformBatchAsync(
        List<ImportFeature> batch,
        int sourceSrid,
        int targetSrid,
        CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Build batch transform query
        var sb = new StringBuilder("SELECT ");
        for (int i = 0; i < batch.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"ST_AsBinary(ST_Transform(ST_GeomFromWKB(@g{i}, @source), @target))");
        }

        await using var cmd = new NpgsqlCommand(sb.ToString(), conn);
        cmd.Parameters.AddWithValue("source", sourceSrid);
        cmd.Parameters.AddWithValue("target", targetSrid);

        for (int i = 0; i < batch.Count; i++)
        {
            var geom = batch[i].Geometry;
            cmd.Parameters.AddWithValue($"g{i}", geom?.AsBinary() ?? DBNull.Value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var results = new List<ImportFeature>(batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            var wkb = reader.IsDBNull(i) ? null : (byte[])reader.GetValue(i);
            var geom = wkb is not null
                ? new NetTopologySuite.IO.WKBReader().Read(wkb)
                : null;

            results.Add(batch[i] with { Geometry = geom });
        }

        return results;
    }
}
```

### Output CRS Support

APIs support `outSR` (FeatureServer) and `crs` (OGC) parameters:

```csharp
// Features/Query/QueryEndpoint.cs (partial)

// FeatureServer: ?outSR=3857
// OGC API Features: ?crs=http://www.opengis.net/def/crs/EPSG/0/3857

private static async Task<IResult> HandleQuery(
    // ... other params
    [FromQuery] int? outSR,          // FeatureServer style
    [FromQuery] string? crs,         // OGC style
    ReprojectionService reprojection,
    CancellationToken ct)
{
    var targetSrid = outSR ?? ParseOgcCrs(crs) ?? layer.Srid;

    var result = await store.QueryAsync(layer.Id, query, ct);

    // Transform if needed
    if (targetSrid != layer.Srid)
    {
        result = await TransformResultAsync(result, layer.Srid, targetSrid, reprojection, ct);
    }

    return FormatResponse(result, targetSrid);
}

private static int? ParseOgcCrs(string? crs)
{
    if (crs is null) return null;

    // Parse http://www.opengis.net/def/crs/EPSG/0/4326
    var match = Regex.Match(crs, @"EPSG/0/(\d+)");
    return match.Success ? int.Parse(match.Groups[1].Value) : null;
}
```

### Supported CRS (via PostGIS)

PostGIS includes ~8000 CRS definitions. Common ones:

| EPSG | Name | Use Case |
|------|------|----------|
| 4326 | WGS 84 | GPS, web mapping |
| 3857 | Web Mercator | Google Maps, OpenStreetMap tiles |
| 4269 | NAD83 | US federal data |
| 2154 | RGF93 / Lambert-93 | France |
| 32632 | UTM Zone 32N | Europe |
| 27700 | OSGB 1936 | UK Ordnance Survey |

Any EPSG code supported by PostGIS works out of the box.

### Layer Metadata Schema Update

```sql
-- Add CRS fields to layers table
ALTER TABLE honua.layers ADD COLUMN srid INT NOT NULL DEFAULT 4326;
ALTER TABLE honua.layers ADD COLUMN srid_name TEXT;  -- e.g., "WGS 84"

-- Query to get layer extent in different CRS
CREATE OR REPLACE FUNCTION honua.get_layer_extent(
    p_layer_id TEXT,
    p_target_srid INT DEFAULT NULL
) RETURNS TABLE (xmin DOUBLE PRECISION, ymin DOUBLE PRECISION,
                 xmax DOUBLE PRECISION, ymax DOUBLE PRECISION) AS $$
DECLARE
    v_table_name TEXT;
    v_geom_col TEXT;
    v_srid INT;
BEGIN
    SELECT table_name, geometry_column, srid
    INTO v_table_name, v_geom_col, v_srid
    FROM honua.layers WHERE id = p_layer_id;

    RETURN QUERY EXECUTE format(
        'SELECT ST_XMin(e), ST_YMin(e), ST_XMax(e), ST_YMax(e)
         FROM (SELECT ST_Extent(%s) AS e FROM %I) sub',
        CASE WHEN p_target_srid IS NOT NULL AND p_target_srid != v_srid
             THEN format('ST_Transform(%I, %s)', v_geom_col, p_target_srid)
             ELSE quote_ident(v_geom_col)
        END,
        v_table_name
    );
END;
$$ LANGUAGE plpgsql;
```

---

## Vector Tiles (MVT)

PostGIS-native vector tile generation using `ST_AsMVT()`.

### Endpoint

```csharp
// Features/VectorTiles/VectorTileEndpoint.cs
public static class VectorTileEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GeoServices-style VectorTileServer
        app.MapGet("/rest/services/{serviceId}/VectorTileServer/tile/{z}/{x}/{y}.pbf",
            HandleTile)
            .WithName("GetVectorTile")
            .Produces(200, contentType: "application/vnd.mapbox-vector-tile")
            .ProducesProblem(404)
            .CacheOutput(p => p.Expire(TimeSpan.FromHours(1)));

        // TileJSON metadata
        app.MapGet("/rest/services/{serviceId}/VectorTileServer/resources/styles/root.json",
            HandleTileJson)
            .WithName("GetTileJson");
    }

    private static async Task<IResult> HandleTile(
        string serviceId, int z, int x, int y,
        VectorTileHandler handler,
        CancellationToken ct)
    {
        var tile = await handler.GetTileAsync(serviceId, z, x, y, ct);

        if (tile is null || tile.Length == 0)
            return Results.NoContent();  // Empty tile

        return Results.Bytes(tile, "application/vnd.mapbox-vector-tile");
    }
}
```

### Handler (PostGIS Native)

```csharp
// Features/VectorTiles/VectorTileHandler.cs
public sealed class VectorTileHandler
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILayerCatalog _catalog;

    public async Task<byte[]?> GetTileAsync(
        string serviceId, int z, int x, int y, CancellationToken ct)
    {
        var layer = await _catalog.GetLayerAsync(serviceId, 0, ct);
        if (layer is null) return null;

        var bounds = TileMath.GetBounds(z, x, y);  // Returns Web Mercator bounds

        var sql = $"""
            SELECT ST_AsMVT(tile, @LayerName, 4096, 'geom') AS mvt
            FROM (
                SELECT
                    {layer.ObjectIdField} AS id,
                    ST_AsMVTGeom(
                        ST_Transform({layer.GeometryField}, 3857),
                        ST_MakeEnvelope(@XMin, @YMin, @XMax, @YMax, 3857),
                        4096, 256, true
                    ) AS geom
                    {BuildAttributeColumns(layer)}
                FROM {layer.TableName}
                WHERE {layer.GeometryField} && ST_Transform(
                    ST_MakeEnvelope(@XMin, @YMin, @XMax, @YMax, 3857),
                    {layer.Srid}
                )
            ) AS tile
            WHERE geom IS NOT NULL
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<byte[]>(sql, new
        {
            LayerName = layer.Name,
            bounds.XMin, bounds.YMin, bounds.XMax, bounds.YMax
        });
    }

    private static string BuildAttributeColumns(LayerDefinition layer)
    {
        var fields = layer.Fields
            .Where(f => !f.IsGeometry && f.Name != layer.ObjectIdField)
            .Take(20)  // Limit attributes in tiles
            .Select(f => f.ColumnName);

        return fields.Any() ? ", " + string.Join(", ", fields) : "";
    }
}
```

### Tile Math Utility

```csharp
// Core/Geometry/TileMath.cs
public static class TileMath
{
    private const double OriginShift = 20037508.342789244;

    public static TileBounds GetBounds(int z, int x, int y)
    {
        var size = OriginShift * 2 / Math.Pow(2, z);
        return new TileBounds(
            XMin: -OriginShift + x * size,
            YMin: OriginShift - (y + 1) * size,
            XMax: -OriginShift + (x + 1) * size,
            YMax: OriginShift - y * size
        );
    }
}

public readonly record struct TileBounds(double XMin, double YMin, double XMax, double YMax);
```

### MVT Performance Safeguards

Vector tile generation can be expensive at low zoom levels (large areas, many features). These safeguards prevent runaway queries.

#### Zoom Level Limits

```csharp
// Features/VectorTiles/VectorTileHandler.cs
private const int MinZoom = 0;   // Allow overview tiles
private const int MaxZoom = 22;  // Standard web map max
private const int SimplifyZoom = 10;  // Below this, simplify geometries

public async Task<byte[]?> GetTileAsync(...)
{
    // Validate zoom level
    if (z < MinZoom || z > MaxZoom)
        return null;

    // Adjust simplification tolerance based on zoom
    var tolerance = z < SimplifyZoom ? Math.Pow(2, SimplifyZoom - z) : 0;

    // ... rest of query with ST_Simplify for low zoom
}
```

#### Feature Count Limits

```csharp
// Limit features per tile to prevent browser overload
private const int MaxFeaturesPerTile = 10_000;

var sql = $"""
    SELECT ST_AsMVT(tile, @LayerName, 4096, 'geom') AS mvt
    FROM (
        SELECT ...
        FROM {layer.TableName}
        WHERE {layer.GeometryField} && ...
        LIMIT {MaxFeaturesPerTile}  -- Hard cap
    ) AS tile
    """;
```

#### Query Timeout

```csharp
// Tile generation timeout (prevent long-running queries)
private static readonly TimeSpan TileTimeout = TimeSpan.FromSeconds(10);

await using var conn = await _dataSource.OpenConnectionAsync(ct);
await using var cmd = new NpgsqlCommand(sql, conn)
{
    CommandTimeout = (int)TileTimeout.TotalSeconds
};
```

#### Geometry Simplification

```sql
-- At low zoom levels, simplify geometries for performance
SELECT ST_AsMVT(tile, @LayerName, 4096, 'geom') AS mvt
FROM (
    SELECT
        {objectIdField} AS id,
        ST_AsMVTGeom(
            CASE
                WHEN @Zoom < 10 THEN ST_Simplify(ST_Transform({geomField}, 3857), @Tolerance)
                ELSE ST_Transform({geomField}, 3857)
            END,
            envelope, 4096, 256, true
        ) AS geom
    FROM ...
) AS tile
```

#### Response Caching

```csharp
// VectorTileEndpoint.cs
app.MapGet("/.../tile/{z}/{x}/{y}.pbf", HandleTile)
    .CacheOutput(p => p
        .Expire(TimeSpan.FromHours(1))
        .Tag("vectortiles")
        .SetVaryByRouteValue("z", "x", "y"));

// Invalidation when layer data changes
await cacheTagProvider.EvictByTagAsync("vectortiles", ct);
```

#### Layer-Level Configuration

```sql
-- Per-layer MVT settings in honua.layers
ALTER TABLE honua.layers ADD COLUMN mvt_min_zoom INT DEFAULT 0;
ALTER TABLE honua.layers ADD COLUMN mvt_max_zoom INT DEFAULT 22;
ALTER TABLE honua.layers ADD COLUMN mvt_max_features INT DEFAULT 10000;
```

---

## OGC API Features

REST/JSON API following OGC API - Features - Part 1: Core.

### Endpoints

```csharp
// Features/OgcFeatures/OgcFeaturesEndpoint.cs
public static class OgcFeaturesEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ogc/features")
            .WithTags("OGC API Features");

        // Landing page
        group.MapGet("/", HandleLandingPage);

        // Conformance
        group.MapGet("/conformance", HandleConformance);

        // Collections
        group.MapGet("/collections", HandleCollections);
        group.MapGet("/collections/{collectionId}", HandleCollection);

        // Items (query)
        group.MapGet("/collections/{collectionId}/items", HandleItems);
        group.MapGet("/collections/{collectionId}/items/{featureId}", HandleItem);

        // Transactions (Part 4)
        group.MapPost("/collections/{collectionId}/items", HandleCreate);
        group.MapPut("/collections/{collectionId}/items/{featureId}", HandleReplace);
        group.MapDelete("/collections/{collectionId}/items/{featureId}", HandleDelete);
    }
}
```

### Conformance Classes (MVP)

```csharp
// Features/OgcFeatures/ConformanceHandler.cs
public static class ConformanceHandler
{
    public static readonly string[] ConformanceClasses = new[]
    {
        // Part 1: Core
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
        "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",

        // Part 4: Transactions (Create/Replace/Delete)
        "http://www.opengis.net/spec/ogcapi-features-4/1.0/conf/create-replace-delete"
    };

    public static IResult Handle() => Results.Ok(new
    {
        conformsTo = ConformanceClasses
    });
}
```

### Items Handler

```csharp
// Features/OgcFeatures/ItemsHandler.cs
public sealed class ItemsHandler
{
    private readonly IFeatureStore _store;
    private readonly ILayerCatalog _catalog;

    public async Task<IResult> HandleAsync(
        string collectionId,
        int? limit,
        int? offset,
        string? bbox,
        string? datetime,
        CancellationToken ct)
    {
        var layer = await _catalog.GetLayerByNameAsync(collectionId, ct);
        if (layer is null)
            return Results.NotFound();

        var query = new FeatureQuery
        {
            Limit = Math.Min(limit ?? 10, 10000),
            Offset = offset ?? 0,
            Bbox = ParseBbox(bbox),
            TimeFilter = ParseDatetime(datetime)
        };

        var result = await _store.QueryAsync(layer.Id, query, ct);

        return Results.Ok(new
        {
            type = "FeatureCollection",
            features = result.Features.Select(f => ToGeoJsonFeature(f, layer)),
            numberMatched = result.TotalCount,
            numberReturned = result.Features.Count,
            links = BuildLinks(collectionId, query, result)
        });
    }
}
```

---

## OData v4

OData enables Excel, Power BI, and other BI tools to query geospatial data directly. MVP implements OData v4 with **full CRUD** operations — same transaction model as FeatureServer and OGC API Features.

### Why OData in MVP

| Scenario | Without OData | With OData |
|----------|---------------|------------|
| Excel user queries layer | Export CSV, import manually | Direct `=OData.Feed()` connection |
| Power BI dashboard | Custom REST connector | Native OData source, auto-schema |
| Tableau analytics | Manual data prep | Live OData connection |
| Migration from GeoServices servers | "Where's OData?" | Familiar workflow |

**OData is table stakes for enterprise GIS** — many organizations use Power BI/Excel for reporting and expect direct connectivity.

### MVP OData Scope

| Feature | In MVP | Notes |
|---------|--------|-------|
| `$select` | ✅ | Choose columns |
| `$filter` | ✅ | eq, ne, gt, lt, ge, le, and, or, not, contains, startswith, endswith |
| `$top` / `$skip` | ✅ | Paging |
| `$count` | ✅ | Total record count |
| `$orderby` | ✅ | Sorting |
| `geo.distance()` | ✅ | Distance filtering → `ST_Distance` |
| `geo.intersects()` | ✅ | Spatial filtering → `ST_Intersects` |
| `geo.length()` | ✅ | Geometry length → `ST_Length` |
| Geometry as GeoJSON | ✅ | `geometry` field as GeoJSON string |
| **POST** (create) | ✅ | Create feature |
| **PATCH** (update) | ✅ | Update feature |
| **DELETE** | ✅ | Delete feature |
| `$expand` | ❌ | Navigation properties deferred |
| `$apply` (aggregation) | ❌ | Deferred to Beta |

### Endpoint Structure

```
/odata/v4
├── /                           # Service root (metadata)
├── /$metadata                  # CSDL schema document
├── /Layers                     # EntitySet: all published layers
├── /Layers({layerId})          # Single layer metadata
├── /Layers({layerId})/Features # EntitySet: features in layer
└── /Layers({layerId})/Features({objectId}) # Single feature
```

**URL Examples — Query:**

```bash
# Service metadata
GET /odata/v4/$metadata

# List layers
GET /odata/v4/Layers

# Query features with filter
GET /odata/v4/Layers('parks')/Features?$filter=area gt 1000&$select=name,area&$top=50

# Get specific feature
GET /odata/v4/Layers('parks')/Features(123)

# Count features
GET /odata/v4/Layers('parks')/Features/$count

# Order and page
GET /odata/v4/Layers('parks')/Features?$orderby=name asc&$skip=100&$top=50

# Spatial: features within 1000 meters of a point
GET /odata/v4/Layers('parks')/Features?$filter=geo.distance(geometry, geography'POINT(-122.4194 37.7749)') lt 1000

# Spatial: features intersecting a bounding box
GET /odata/v4/Layers('parks')/Features?$filter=geo.intersects(geometry, geography'POLYGON((-122.5 37.7, -122.3 37.7, -122.3 37.8, -122.5 37.8, -122.5 37.7))')

# Spatial: routes longer than 5km
GET /odata/v4/Layers('trails')/Features?$filter=geo.length(geometry) gt 5000
```

**URL Examples — CRUD:**

```bash
# Create feature
POST /odata/v4/Layers('parks')/Features
Content-Type: application/json

{
  "name": "Central Park",
  "area": 3410000,
  "geometry": {"type": "Polygon", "coordinates": [...]}
}
# Response: 201 Created with Location header

# Update feature (partial)
PATCH /odata/v4/Layers('parks')/Features(123)
Content-Type: application/json

{
  "name": "Central Park Updated"
}
# Response: 200 OK with updated entity

# Delete feature
DELETE /odata/v4/Layers('parks')/Features(123)
# Response: 204 No Content
```

### Implementation Approach

**No Microsoft.AspNetCore.OData** — that library is heavy (~15 dependencies) and reflection-based (breaks AOT). Instead, implement a minimal OData parser.

#### Project Structure

```
Features/OData/
├── ODataEndpoints.cs           # Minimal API mappings (GET/POST/PATCH/DELETE)
├── ODataQueryParser.cs         # Parse $filter, $select, etc.
├── ODataFilterTranslator.cs    # Convert OData filter → SQL WHERE
├── ODataMetadataGenerator.cs   # Generate $metadata CSDL
├── ODataResponseFormatter.cs   # Format OData JSON responses
├── ODataRequestParser.cs       # Parse POST/PATCH request bodies
├── ODataCrudHandler.cs         # Create/Update/Delete operations
└── Models/
    ├── ODataQueryOptions.cs    # Parsed query options
    ├── ODataEntitySet.cs       # EntitySet wrapper
    └── ODataValue.cs           # Single entity wrapper
```

#### OData Query Parser

```csharp
// Features/OData/ODataQueryParser.cs
namespace Honua.Server.Features.OData;

public sealed class ODataQueryParser
{
    public ODataQueryOptions Parse(HttpRequest request)
    {
        var query = request.Query;

        return new ODataQueryOptions
        {
            Select = ParseSelect(query["$select"].FirstOrDefault()),
            Filter = ParseFilter(query["$filter"].FirstOrDefault()),
            OrderBy = ParseOrderBy(query["$orderby"].FirstOrDefault()),
            Top = ParseInt(query["$top"].FirstOrDefault()),
            Skip = ParseInt(query["$skip"].FirstOrDefault()),
            Count = ParseBool(query["$count"].FirstOrDefault())
        };
    }

    private IReadOnlyList<string>? ParseSelect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private ODataFilter? ParseFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ODataFilterParser.Parse(value);
    }

    private IReadOnlyList<ODataOrderBy>? ParseOrderBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Split(',')
            .Select(ParseSingleOrderBy)
            .ToList();
    }

    private ODataOrderBy ParseSingleOrderBy(string value)
    {
        var parts = value.Trim().Split(' ', 2);
        var field = parts[0];
        var direction = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase)
            ? OrderDirection.Descending
            : OrderDirection.Ascending;
        return new ODataOrderBy(field, direction);
    }

    private int? ParseInt(string? value) =>
        int.TryParse(value, out var i) ? i : null;

    private bool ParseBool(string? value) =>
        bool.TryParse(value, out var b) && b;
}

public record ODataQueryOptions
{
    public IReadOnlyList<string>? Select { get; init; }
    public ODataFilter? Filter { get; init; }
    public IReadOnlyList<ODataOrderBy>? OrderBy { get; init; }
    public int? Top { get; init; }
    public int? Skip { get; init; }
    public bool Count { get; init; }
}

public record ODataOrderBy(string Field, OrderDirection Direction);
public enum OrderDirection { Ascending, Descending }
```

#### OData Filter Parser

```csharp
// Features/OData/ODataFilterParser.cs
namespace Honua.Server.Features.OData;

/// <summary>
/// Simple recursive-descent parser for OData $filter expressions.
/// Supports: eq, ne, gt, lt, ge, le, and, or, not, contains(), startswith(), endswith()
/// </summary>
public static class ODataFilterParser
{
    public static ODataFilter Parse(string filter)
    {
        var tokens = Tokenize(filter);
        var index = 0;
        return ParseExpression(tokens, ref index);
    }

    private static ODataFilter ParseExpression(List<Token> tokens, ref int index)
    {
        var left = ParseAndExpression(tokens, ref index);

        while (index < tokens.Count && tokens[index].Type == TokenType.Or)
        {
            index++; // consume 'or'
            var right = ParseAndExpression(tokens, ref index);
            left = new ODataFilter.Or(left, right);
        }

        return left;
    }

    private static ODataFilter ParseAndExpression(List<Token> tokens, ref int index)
    {
        var left = ParseUnaryExpression(tokens, ref index);

        while (index < tokens.Count && tokens[index].Type == TokenType.And)
        {
            index++; // consume 'and'
            var right = ParseUnaryExpression(tokens, ref index);
            left = new ODataFilter.And(left, right);
        }

        return left;
    }

    private static ODataFilter ParseUnaryExpression(List<Token> tokens, ref int index)
    {
        if (index < tokens.Count && tokens[index].Type == TokenType.Not)
        {
            index++; // consume 'not'
            var operand = ParsePrimaryExpression(tokens, ref index);
            return new ODataFilter.Not(operand);
        }

        return ParsePrimaryExpression(tokens, ref index);
    }

    private static ODataFilter ParsePrimaryExpression(List<Token> tokens, ref int index)
    {
        // Function calls: contains(field, 'value'), startswith(), endswith()
        if (tokens[index].Type == TokenType.Identifier &&
            index + 1 < tokens.Count && tokens[index + 1].Type == TokenType.OpenParen)
        {
            return ParseFunctionCall(tokens, ref index);
        }

        // Parenthesized expression
        if (tokens[index].Type == TokenType.OpenParen)
        {
            index++; // consume '('
            var expr = ParseExpression(tokens, ref index);
            if (tokens[index].Type != TokenType.CloseParen)
                throw new ODataParseException("Expected ')'");
            index++; // consume ')'
            return expr;
        }

        // Comparison: field op value
        return ParseComparison(tokens, ref index);
    }

    private static ODataFilter ParseComparison(List<Token> tokens, ref int index)
    {
        var field = tokens[index++].Value;

        var opToken = tokens[index++];
        var op = opToken.Type switch
        {
            TokenType.Eq => ComparisonOp.Equal,
            TokenType.Ne => ComparisonOp.NotEqual,
            TokenType.Gt => ComparisonOp.GreaterThan,
            TokenType.Lt => ComparisonOp.LessThan,
            TokenType.Ge => ComparisonOp.GreaterThanOrEqual,
            TokenType.Le => ComparisonOp.LessThanOrEqual,
            _ => throw new ODataParseException($"Expected comparison operator, got {opToken.Type}")
        };

        var valueToken = tokens[index++];
        object value = valueToken.Type switch
        {
            TokenType.String => valueToken.Value,
            TokenType.Number => ParseNumber(valueToken.Value),
            TokenType.True => true,
            TokenType.False => false,
            TokenType.Null => null!,
            _ => throw new ODataParseException($"Expected value, got {valueToken.Type}")
        };

        return new ODataFilter.Comparison(field, op, value);
    }

    private static ODataFilter ParseFunctionCall(List<Token> tokens, ref int index)
    {
        var funcName = tokens[index++].Value.ToLowerInvariant();
        index++; // consume '('

        var field = tokens[index++].Value;

        if (tokens[index].Type != TokenType.Comma)
            throw new ODataParseException("Expected ','");
        index++; // consume ','

        var valueToken = tokens[index++];
        var value = valueToken.Value;

        if (tokens[index].Type != TokenType.CloseParen)
            throw new ODataParseException("Expected ')'");
        index++; // consume ')'

        return funcName switch
        {
            "contains" => new ODataFilter.Contains(field, value),
            "startswith" => new ODataFilter.StartsWith(field, value),
            "endswith" => new ODataFilter.EndsWith(field, value),
            _ => throw new ODataParseException($"Unknown function: {funcName}")
        };
    }

    // Tokenizer implementation omitted for brevity
    private static List<Token> Tokenize(string filter) { /* ... */ }
    private static object ParseNumber(string value) { /* ... */ }
}

// Filter AST
public abstract record ODataFilter
{
    public sealed record Comparison(string Field, ComparisonOp Op, object Value) : ODataFilter;
    public sealed record And(ODataFilter Left, ODataFilter Right) : ODataFilter;
    public sealed record Or(ODataFilter Left, ODataFilter Right) : ODataFilter;
    public sealed record Not(ODataFilter Operand) : ODataFilter;
    public sealed record Contains(string Field, string Value) : ODataFilter;
    public sealed record StartsWith(string Field, string Value) : ODataFilter;
    public sealed record EndsWith(string Field, string Value) : ODataFilter;

    // Spatial functions
    public sealed record GeoDistance(string Field, ODataGeography Point) : ODataFilter;
    public sealed record GeoIntersects(string Field, ODataGeography Geometry) : ODataFilter;
    public sealed record GeoLength(string Field) : ODataFilter;
}

// OData geography literal (parsed from geography'POINT(...)')
public abstract record ODataGeography
{
    public sealed record Point(double Longitude, double Latitude) : ODataGeography;
    public sealed record Polygon(IReadOnlyList<(double Lon, double Lat)> Ring) : ODataGeography;
    public sealed record LineString(IReadOnlyList<(double Lon, double Lat)> Coords) : ODataGeography;

    public string ToWkt() => this switch
    {
        Point p => $"POINT({p.Longitude} {p.Latitude})",
        Polygon poly => $"POLYGON(({string.Join(", ", poly.Ring.Select(c => $"{c.Lon} {c.Lat}"))}))",
        LineString ls => $"LINESTRING({string.Join(", ", ls.Coords.Select(c => $"{c.Lon} {c.Lat}"))})",
        _ => throw new InvalidOperationException()
    };
}

public enum ComparisonOp { Equal, NotEqual, GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual }
```

#### OData → SQL Translation

```csharp
// Features/OData/ODataFilterTranslator.cs
namespace Honua.Server.Features.OData;

public sealed class ODataFilterTranslator
{
    private readonly LayerDefinition _layer;
    private readonly List<NpgsqlParameter> _parameters = new();
    private int _paramIndex = 0;

    public ODataFilterTranslator(LayerDefinition layer) => _layer = layer;

    public (string Sql, IReadOnlyList<NpgsqlParameter> Parameters) Translate(ODataFilter filter)
    {
        var sql = TranslateNode(filter);
        return (sql, _parameters);
    }

    private string TranslateNode(ODataFilter filter) => filter switch
    {
        ODataFilter.Comparison c => TranslateComparison(c),
        ODataFilter.And a => $"({TranslateNode(a.Left)} AND {TranslateNode(a.Right)})",
        ODataFilter.Or o => $"({TranslateNode(o.Left)} OR {TranslateNode(o.Right)})",
        ODataFilter.Not n => $"NOT ({TranslateNode(n.Operand)})",
        ODataFilter.Contains c => TranslateLike(c.Field, $"%{c.Value}%"),
        ODataFilter.StartsWith s => TranslateLike(s.Field, $"{s.Value}%"),
        ODataFilter.EndsWith e => TranslateLike(e.Field, $"%{e.Value}"),
        ODataFilter.GeoDistance d => TranslateGeoDistance(d),
        ODataFilter.GeoIntersects i => TranslateGeoIntersects(i),
        ODataFilter.GeoLength l => TranslateGeoLength(l),
        _ => throw new InvalidOperationException($"Unknown filter type: {filter.GetType()}")
    };

    // geo.distance(geometry, geography'POINT(...)') → ST_Distance
    // Returns distance expression that can be compared (e.g., < 1000)
    private string TranslateGeoDistance(ODataFilter.GeoDistance d)
    {
        var geomCol = GetGeometryColumn();
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(new NpgsqlParameter(paramName, d.Point.ToWkt()));

        // ST_Distance returns meters when using geography type
        return $"ST_Distance({geomCol}::geography, ST_GeomFromText({paramName}, 4326)::geography)";
    }

    // geo.intersects(geometry, geography'POLYGON(...)') → ST_Intersects
    private string TranslateGeoIntersects(ODataFilter.GeoIntersects i)
    {
        var geomCol = GetGeometryColumn();
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(new NpgsqlParameter(paramName, i.Geometry.ToWkt()));

        return $"ST_Intersects({geomCol}, ST_GeomFromText({paramName}, 4326))";
    }

    // geo.length(geometry) → ST_Length (meters)
    private string TranslateGeoLength(ODataFilter.GeoLength l)
    {
        var geomCol = GetGeometryColumn();
        return $"ST_Length({geomCol}::geography)";
    }

    private string GetGeometryColumn() =>
        $"\"{_layer.GeometryField ?? "geom"}\"";

    private string TranslateComparison(ODataFilter.Comparison c)
    {
        var column = ValidateAndQuoteColumn(c.Field);
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(new NpgsqlParameter(paramName, c.Value ?? DBNull.Value));

        var op = c.Op switch
        {
            ComparisonOp.Equal => c.Value is null ? "IS" : "=",
            ComparisonOp.NotEqual => c.Value is null ? "IS NOT" : "<>",
            ComparisonOp.GreaterThan => ">",
            ComparisonOp.LessThan => "<",
            ComparisonOp.GreaterThanOrEqual => ">=",
            ComparisonOp.LessThanOrEqual => "<=",
            _ => throw new InvalidOperationException()
        };

        return c.Value is null
            ? $"{column} {op} NULL"
            : $"{column} {op} {paramName}";
    }

    private string TranslateLike(string field, string pattern)
    {
        var column = ValidateAndQuoteColumn(field);
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(new NpgsqlParameter(paramName, pattern));
        return $"{column} ILIKE {paramName}";
    }

    private string ValidateAndQuoteColumn(string field)
    {
        // Validate field exists in layer schema
        var fieldDef = _layer.Fields.FirstOrDefault(f =>
            f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));

        if (fieldDef is null)
            throw new ODataParseException($"Unknown field: {field}");

        // Quote to prevent SQL injection
        return $"\"{fieldDef.Name}\"";
    }
}
```

#### OData Endpoints

```csharp
// Features/OData/ODataEndpoints.cs
namespace Honua.Server.Features.OData;

public static class ODataEndpoints
{
    public static void MapODataEndpoints(this IEndpointRouteBuilder app)
    {
        var odata = app.MapGroup("/odata/v4")
            .WithTags("OData");

        // Query endpoints
        odata.MapGet("/", HandleServiceRoot);
        odata.MapGet("/$metadata", HandleMetadata);
        odata.MapGet("/Layers", HandleLayers);
        odata.MapGet("/Layers('{layerId}')", HandleLayer);
        odata.MapGet("/Layers('{layerId}')/Features", HandleFeatures);
        odata.MapGet("/Layers('{layerId}')/Features({objectId:long})", HandleFeature);
        odata.MapGet("/Layers('{layerId}')/Features/$count", HandleFeaturesCount);

        // CRUD endpoints
        odata.MapPost("/Layers('{layerId}')/Features", HandleCreateFeature);
        odata.MapPatch("/Layers('{layerId}')/Features({objectId:long})", HandleUpdateFeature);
        odata.MapDelete("/Layers('{layerId}')/Features({objectId:long})", HandleDeleteFeature);
    }

    private static async Task<IResult> HandleServiceRoot(
        HttpContext ctx,
        ILayerCatalog catalog,
        CancellationToken ct)
    {
        var layers = await catalog.GetAllLayersAsync(ct);

        return Results.Ok(new
        {
            @odata_context = $"{ctx.Request.Scheme}://{ctx.Request.Host}/odata/v4/$metadata",
            value = layers.Select(l => new
            {
                name = "Layers",
                kind = "EntitySet",
                url = $"Layers('{l.Id}')"
            })
        });
    }

    private static async Task<IResult> HandleMetadata(
        HttpContext ctx,
        ILayerCatalog catalog,
        CancellationToken ct)
    {
        var layers = await catalog.GetAllLayersAsync(ct);
        var csdl = ODataMetadataGenerator.GenerateCsdl(layers);

        return Results.Content(csdl, "application/xml");
    }

    private static async Task<IResult> HandleFeatures(
        HttpContext ctx,
        string layerId,
        ILayerCatalog catalog,
        IFeatureStore store,
        ODataQueryParser parser,
        CancellationToken ct)
    {
        var layer = await catalog.GetLayerAsync(layerId, ct);
        if (layer is null)
            return Results.NotFound(ODataError("LayerNotFound", $"Layer '{layerId}' not found"));

        var options = parser.Parse(ctx.Request);

        // Translate OData options to FeatureQuery
        var query = TranslateToFeatureQuery(layer, options);

        var result = await store.QueryAsync(layer.Id, query, ct);

        var response = new Dictionary<string, object>
        {
            ["@odata.context"] = $"{ctx.Request.Scheme}://{ctx.Request.Host}/odata/v4/$metadata#Layers('{layerId}')/Features",
            ["value"] = result.Features.Select(f => FormatFeature(f, layer, options.Select))
        };

        if (options.Count)
            response["@odata.count"] = result.TotalCount;

        // Next link for paging
        if (result.Features.Count == (options.Top ?? 100) && result.TotalCount > (options.Skip ?? 0) + result.Features.Count)
        {
            var nextSkip = (options.Skip ?? 0) + result.Features.Count;
            response["@odata.nextLink"] = BuildNextLink(ctx.Request, nextSkip);
        }

        return Results.Ok(response);
    }

    private static FeatureQuery TranslateToFeatureQuery(LayerDefinition layer, ODataQueryOptions options)
    {
        var query = new FeatureQuery
        {
            Limit = Math.Min(options.Top ?? 100, 10000),
            Offset = options.Skip ?? 0,
            OutFields = options.Select?.ToList()
        };

        if (options.Filter is not null)
        {
            var translator = new ODataFilterTranslator(layer);
            var (whereSql, parameters) = translator.Translate(options.Filter);
            query.WhereClause = whereSql;
            query.Parameters = parameters;
        }

        if (options.OrderBy is { Count: > 0 })
        {
            query.OrderByFields = options.OrderBy
                .Select(o => new OrderByField(o.Field, o.Direction == OrderDirection.Ascending))
                .ToList();
        }

        return query;
    }

    private static object FormatFeature(
        FeatureRecord feature,
        LayerDefinition layer,
        IReadOnlyList<string>? select)
    {
        var result = new Dictionary<string, object?>
        {
            [layer.ObjectIdField] = feature.ObjectId
        };

        foreach (var (key, value) in feature.Attributes)
        {
            if (select is null || select.Contains(key, StringComparer.OrdinalIgnoreCase))
                result[key] = value;
        }

        // Include geometry as GeoJSON string
        if (feature.Geometry is not null && (select is null || select.Contains("geometry", StringComparer.OrdinalIgnoreCase)))
        {
            result["geometry"] = feature.Geometry.ToGeoJson();
        }

        return result;
    }

    private static object ODataError(string code, string message) => new
    {
        error = new { code, message }
    };
}
```

#### CSDL Metadata Generator

```csharp
// Features/OData/ODataMetadataGenerator.cs
namespace Honua.Server.Features.OData;

public static class ODataMetadataGenerator
{
    public static string GenerateCsdl(IReadOnlyList<LayerDefinition> layers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            <?xml version="1.0" encoding="utf-8"?>
            <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
              <edmx:DataServices>
                <Schema Namespace="Honua.OData" xmlns="http://docs.oasis-open.org/odata/ns/edm">
            """);

        foreach (var layer in layers)
        {
            sb.AppendLine($"""
                  <EntityType Name="{EscapeXml(layer.Name)}Feature">
                    <Key>
                      <PropertyRef Name="{layer.ObjectIdField}"/>
                    </Key>
                    <Property Name="{layer.ObjectIdField}" Type="Edm.Int64" Nullable="false"/>
            """);

            foreach (var field in layer.Fields.Where(f => f.Name != layer.ObjectIdField))
            {
                var edmType = ToEdmType(field.Type);
                sb.AppendLine($"""
                        <Property Name="{EscapeXml(field.Name)}" Type="{edmType}" Nullable="true"/>
                """);
            }

            if (layer.GeometryType is not null)
            {
                sb.AppendLine("""
                        <Property Name="geometry" Type="Edm.String" Nullable="true"/>
                """);
            }

            sb.AppendLine("      </EntityType>");
        }

        sb.AppendLine("""
                  <EntityContainer Name="HonuaContainer">
            """);

        foreach (var layer in layers)
        {
            sb.AppendLine($"""
                    <EntitySet Name="Layers('{EscapeXml(layer.Id)}')/Features" EntityType="Honua.OData.{EscapeXml(layer.Name)}Feature"/>
            """);
        }

        sb.AppendLine("""
                  </EntityContainer>
                </Schema>
              </edmx:DataServices>
            </edmx:Edmx>
            """);

        return sb.ToString();
    }

    private static string ToEdmType(FieldType type) => type switch
    {
        FieldType.String => "Edm.String",
        FieldType.Integer => "Edm.Int32",
        FieldType.Long => "Edm.Int64",
        FieldType.Double => "Edm.Double",
        FieldType.Date => "Edm.Date",
        FieldType.DateTime => "Edm.DateTimeOffset",
        FieldType.Boolean => "Edm.Boolean",
        FieldType.Guid => "Edm.Guid",
        _ => "Edm.String"
    };

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
```

### OData JSON Source Generation (AOT)

```csharp
// Features/OData/ODataJsonContext.cs
namespace Honua.Server.Features.OData;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ODataServiceRoot))]
[JsonSerializable(typeof(ODataEntitySetResponse))]
[JsonSerializable(typeof(ODataSingleEntityResponse))]
[JsonSerializable(typeof(ODataError))]
public partial class ODataJsonContext : JsonSerializerContext { }

public record ODataServiceRoot(
    [property: JsonPropertyName("@odata.context")] string Context,
    IReadOnlyList<ODataEntitySetInfo> Value);

public record ODataEntitySetInfo(string Name, string Kind, string Url);

public record ODataEntitySetResponse(
    [property: JsonPropertyName("@odata.context")] string Context,
    [property: JsonPropertyName("@odata.count")] long? Count,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink,
    IReadOnlyList<Dictionary<string, object?>> Value);

public record ODataError(ODataErrorDetails Error);
public record ODataErrorDetails(string Code, string Message);
```

### OData CRUD Handlers

```csharp
// Features/OData/ODataCrudHandler.cs
namespace Honua.Server.Features.OData;

public sealed class ODataCrudHandler
{
    private readonly IFeatureStore _store;
    private readonly ILayerCatalog _catalog;
    private readonly ILogger<ODataCrudHandler> _logger;

    public ODataCrudHandler(IFeatureStore store, ILayerCatalog catalog, ILogger<ODataCrudHandler> logger)
    {
        _store = store;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<IResult> CreateFeatureAsync(
        string layerId,
        HttpContext ctx,
        CancellationToken ct)
    {
        var layer = await _catalog.GetLayerAsync(layerId, ct);
        if (layer is null)
            return Results.NotFound(ODataError("LayerNotFound", $"Layer '{layerId}' not found"));

        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(ct);
        if (body is null)
            return Results.BadRequest(ODataError("InvalidBody", "Request body is required"));

        // Parse geometry from GeoJSON string
        var edit = ParseFeatureEdit(layer, body);

        try
        {
            var created = await _store.CreateAsync(layer.Id, edit, ct);

            var location = $"{ctx.Request.Scheme}://{ctx.Request.Host}/odata/v4/Layers('{layerId}')/Features({created.ObjectId})";
            ctx.Response.Headers.Location = location;

            return Results.Created(location, FormatFeature(created, layer, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create feature in layer {LayerId}", layerId);
            return Results.BadRequest(ODataError("CreateFailed", ex.Message));
        }
    }

    public async Task<IResult> UpdateFeatureAsync(
        string layerId,
        long objectId,
        HttpContext ctx,
        CancellationToken ct)
    {
        var layer = await _catalog.GetLayerAsync(layerId, ct);
        if (layer is null)
            return Results.NotFound(ODataError("LayerNotFound", $"Layer '{layerId}' not found"));

        var existing = await _store.GetAsync(layer.Id, objectId.ToString(), ct);
        if (existing is null)
            return Results.NotFound(ODataError("FeatureNotFound", $"Feature {objectId} not found"));

        var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object?>>(ct);
        if (body is null)
            return Results.BadRequest(ODataError("InvalidBody", "Request body is required"));

        var edit = ParseFeatureEdit(layer, body);

        try
        {
            var updated = await _store.UpdateAsync(layer.Id, objectId.ToString(), edit, ct);
            return Results.Ok(FormatFeature(updated, layer, null));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feature {ObjectId} in layer {LayerId}", objectId, layerId);
            return Results.BadRequest(ODataError("UpdateFailed", ex.Message));
        }
    }

    public async Task<IResult> DeleteFeatureAsync(
        string layerId,
        long objectId,
        CancellationToken ct)
    {
        var layer = await _catalog.GetLayerAsync(layerId, ct);
        if (layer is null)
            return Results.NotFound(ODataError("LayerNotFound", $"Layer '{layerId}' not found"));

        var deleted = await _store.DeleteAsync(layer.Id, objectId.ToString(), ct);

        return deleted
            ? Results.NoContent()
            : Results.NotFound(ODataError("FeatureNotFound", $"Feature {objectId} not found"));
    }

    private static FeatureEdit ParseFeatureEdit(LayerDefinition layer, Dictionary<string, object?> body)
    {
        var attributes = new Dictionary<string, object?>();
        Geometry? geometry = null;

        foreach (var (key, value) in body)
        {
            if (key.Equals("geometry", StringComparison.OrdinalIgnoreCase) && value is string geoJson)
            {
                geometry = GeometryParser.FromGeoJson(geoJson);
            }
            else if (!key.Equals(layer.ObjectIdField, StringComparison.OrdinalIgnoreCase))
            {
                // Validate field exists
                var field = layer.Fields.FirstOrDefault(f =>
                    f.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (field is not null)
                {
                    attributes[field.Name] = ConvertValue(value, field.Type);
                }
            }
        }

        return new FeatureEdit { Attributes = attributes, Geometry = geometry };
    }

    private static object? ConvertValue(object? value, FieldType targetType)
    {
        if (value is null) return null;

        return targetType switch
        {
            FieldType.Integer when value is JsonElement je => je.GetInt32(),
            FieldType.Long when value is JsonElement je => je.GetInt64(),
            FieldType.Double when value is JsonElement je => je.GetDouble(),
            FieldType.Boolean when value is JsonElement je => je.GetBoolean(),
            FieldType.DateTime when value is JsonElement je => je.GetDateTime(),
            _ => value?.ToString()
        };
    }

    private static object ODataError(string code, string message) => new
    {
        error = new { code, message }
    };

    private static object FormatFeature(FeatureRecord feature, LayerDefinition layer, IReadOnlyList<string>? select)
    {
        var result = new Dictionary<string, object?>
        {
            [layer.ObjectIdField] = feature.ObjectId
        };

        foreach (var (key, value) in feature.Attributes)
        {
            if (select is null || select.Contains(key, StringComparer.OrdinalIgnoreCase))
                result[key] = value;
        }

        if (feature.Geometry is not null)
        {
            result["geometry"] = feature.Geometry.ToGeoJson();
        }

        return result;
    }
}
```

The CRUD operations use the same `IFeatureStore` abstraction as FeatureServer and OGC API Features, ensuring consistent transaction behavior across all three protocols.

### Testing OData

```csharp
// Tests/OData/ODataFilterParserTests.cs
public class ODataFilterParserTests
{
    [Theory]
    [InlineData("name eq 'test'", "name", ComparisonOp.Equal, "test")]
    [InlineData("age gt 21", "age", ComparisonOp.GreaterThan, 21)]
    [InlineData("active eq true", "active", ComparisonOp.Equal, true)]
    public void Parse_SimpleComparison_ReturnsComparison(
        string filter, string expectedField, ComparisonOp expectedOp, object expectedValue)
    {
        var result = ODataFilterParser.Parse(filter);

        var comparison = Assert.IsType<ODataFilter.Comparison>(result);
        Assert.Equal(expectedField, comparison.Field);
        Assert.Equal(expectedOp, comparison.Op);
        Assert.Equal(expectedValue, comparison.Value);
    }

    [Fact]
    public void Parse_Contains_ReturnsContainsFilter()
    {
        var result = ODataFilterParser.Parse("contains(name, 'park')");

        var contains = Assert.IsType<ODataFilter.Contains>(result);
        Assert.Equal("name", contains.Field);
        Assert.Equal("park", contains.Value);
    }

    [Fact]
    public void Parse_ComplexExpression_ReturnsCorrectAst()
    {
        var result = ODataFilterParser.Parse("(status eq 'active' or status eq 'pending') and age gt 18");

        var and = Assert.IsType<ODataFilter.And>(result);
        var or = Assert.IsType<ODataFilter.Or>(and.Left);
        Assert.IsType<ODataFilter.Comparison>(and.Right);
    }
}

// Tests/OData/ODataEndpointsTests.cs
public class ODataEndpointsTests : IClassFixture<HonuaWebApplicationFactory>
{
    [Fact]
    public async Task GetFeatures_WithFilter_ReturnsFilteredResults()
    {
        var response = await _client.GetAsync(
            "/odata/v4/Layers('parks')/Features?$filter=area gt 1000&$top=10");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();

        Assert.NotNull(json);
        var features = json.RootElement.GetProperty("value").EnumerateArray().ToList();
        Assert.All(features, f => Assert.True(f.GetProperty("area").GetDouble() > 1000));
    }

    [Fact]
    public async Task GetFeatures_WithSelect_ReturnsOnlySelectedFields()
    {
        var response = await _client.GetAsync(
            "/odata/v4/Layers('parks')/Features?$select=name,area");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>();

        var feature = json!.RootElement.GetProperty("value").EnumerateArray().First();
        Assert.True(feature.TryGetProperty("name", out _));
        Assert.True(feature.TryGetProperty("area", out _));
        Assert.False(feature.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task GetMetadata_ReturnsCsdl()
    {
        var response = await _client.GetAsync("/odata/v4/$metadata");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("edmx:Edmx", content);
    }
}
```

### Excel/Power BI Integration Notes

**Excel:**
```
=OData.Feed("https://honua.example.com/odata/v4/Layers('parcels')/Features")
```

**Power BI:**
1. Get Data → OData Feed
2. Enter URL: `https://honua.example.com/odata/v4/Layers('parcels')/Features`
3. Power BI auto-discovers schema from `$metadata`

**Known Limitations:**
- Geometry returned as GeoJSON string (Power BI can't visualize directly — use Power BI's built-in map with lat/lon columns)
- Read-only — no Power BI writeback scenarios

---

## Admin UI (Blazor WASM + MapLibre GL)

The admin interface is a Blazor WebAssembly app with MapLibre GL JS for map previews.

### Technology Choices

| Component | Choice | Rationale |
|-----------|--------|-----------|
| **Framework** | Blazor WASM | C# end-to-end, single language |
| **Map library** | MapLibre GL JS | Native MVT support, WebGL performance |
| **JS Interop** | Minimal wrapper | Keep it simple, call MapLibre directly |
| **Styling** | Tailwind CSS | Utility-first, works well with Blazor |

### Why MapLibre GL over Leaflet

| Factor | Leaflet | MapLibre GL |
|--------|---------|-------------|
| **MVT support** | Plugin required | Native ✅ |
| **Performance** | DOM-based | WebGL ✅ |
| **Vector styles** | Limited | Full Mapbox spec ✅ |
| **Bundle size** | ~40KB | ~200KB |
| **Future-proof** | Raster-centric | Vector-native ✅ |

Since we're serving MVT, MapLibre GL's native vector tile support is the right choice despite the larger bundle.

### Project Structure

```
src/Honua.Admin/
├── wwwroot/
│   ├── index.html
│   ├── css/
│   │   └── app.css                    # Tailwind output
│   └── js/
│       └── maplibre-interop.js        # MapLibre JS interop
├── Pages/
│   ├── Index.razor                    # Dashboard
│   ├── Connections/
│   │   ├── ConnectionList.razor       # List PostGIS connections
│   │   └── ConnectionForm.razor       # Add/edit connection
│   ├── Layers/
│   │   ├── LayerList.razor            # List layers
│   │   ├── LayerDetail.razor          # Layer settings
│   │   └── LayerPreview.razor         # MapLibre preview
│   ├── Import/
│   │   ├── FileImport.razor           # Upload GeoJSON/Shapefile/etc.
│   │   ├── GeoServicesImport.razor    # GeoServices REST import wizard
│   │   └── ImportPreview.razor        # Preview before import
│   └── Health/
│       └── HealthDashboard.razor      # Service health
├── Components/
│   ├── MapPreview.razor               # MapLibre GL wrapper
│   ├── FileUpload.razor               # Drag-drop file upload
│   ├── CrsSelector.razor              # EPSG code picker
│   └── DataGrid.razor                 # Attribute table
├── Services/
│   ├── HonuaApiClient.cs              # Typed HTTP client
│   ├── MapInterop.cs                  # MapLibre JS interop service
│   └── ImportService.cs               # File import orchestration
└── Program.cs                         # WASM entry point
```

### MapLibre GL Interop

```javascript
// wwwroot/js/maplibre-interop.js
window.maplibreInterop = {
    maps: {},

    createMap: function(containerId, options) {
        const map = new maplibregl.Map({
            container: containerId,
            style: options.style || {
                version: 8,
                sources: {},
                layers: [{
                    id: 'background',
                    type: 'background',
                    paint: { 'background-color': '#f0f0f0' }
                }]
            },
            center: options.center || [0, 0],
            zoom: options.zoom || 2
        });

        this.maps[containerId] = map;
        return true;
    },

    addVectorTileSource: function(containerId, sourceId, tilesUrl) {
        const map = this.maps[containerId];
        if (!map) return false;

        map.addSource(sourceId, {
            type: 'vector',
            tiles: [tilesUrl],
            minzoom: 0,
            maxzoom: 14
        });
        return true;
    },

    addLayer: function(containerId, layerConfig) {
        const map = this.maps[containerId];
        if (!map) return false;

        map.addLayer(layerConfig);
        return true;
    },

    fitBounds: function(containerId, bounds, padding) {
        const map = this.maps[containerId];
        if (!map) return false;

        map.fitBounds(bounds, { padding: padding || 50 });
        return true;
    },

    removeMap: function(containerId) {
        const map = this.maps[containerId];
        if (map) {
            map.remove();
            delete this.maps[containerId];
        }
        return true;
    }
};
```

### Blazor MapLibre Component

```csharp
// Components/MapPreview.razor
@inject IJSRuntime JS
@implements IAsyncDisposable

<div id="@_containerId" class="w-full h-96 rounded-lg border"></div>

@code {
    [Parameter] public string? LayerId { get; set; }
    [Parameter] public double[]? Center { get; set; }
    [Parameter] public int Zoom { get; set; } = 10;
    [Parameter] public double[]? Bounds { get; set; }

    private string _containerId = $"map-{Guid.NewGuid():N}";
    private bool _mapCreated;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JS.InvokeVoidAsync("maplibreInterop.createMap", _containerId, new
            {
                center = Center ?? new[] { 0.0, 0.0 },
                zoom = Zoom
            });
            _mapCreated = true;

            if (LayerId is not null)
            {
                await AddLayerAsync(LayerId);
            }

            if (Bounds is { Length: 4 })
            {
                await JS.InvokeVoidAsync("maplibreInterop.fitBounds", _containerId,
                    new[] { new[] { Bounds[0], Bounds[1] }, new[] { Bounds[2], Bounds[3] } });
            }
        }
    }

    public async Task AddLayerAsync(string layerId)
    {
        if (!_mapCreated) return;

        var tilesUrl = $"/rest/services/default/VectorTileServer/{layerId}/tile/{{z}}/{{x}}/{{y}}.pbf";

        await JS.InvokeVoidAsync("maplibreInterop.addVectorTileSource",
            _containerId, $"source-{layerId}", tilesUrl);

        await JS.InvokeVoidAsync("maplibreInterop.addLayer", _containerId, new
        {
            id = $"layer-{layerId}-fill",
            type = "fill",
            source = $"source-{layerId}",
            sourceLayer = layerId,
            paint = new
            {
                fillColor = "#088",
                fillOpacity = 0.5
            }
        });

        await JS.InvokeVoidAsync("maplibreInterop.addLayer", _containerId, new
        {
            id = $"layer-{layerId}-outline",
            type = "line",
            source = $"source-{layerId}",
            sourceLayer = layerId,
            paint = new
            {
                lineColor = "#044",
                lineWidth = 1
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_mapCreated)
        {
            await JS.InvokeVoidAsync("maplibreInterop.removeMap", _containerId);
        }
    }
}
```

### Layer Preview Page

```csharp
// Pages/Layers/LayerPreview.razor
@page "/layers/{LayerId}/preview"
@inject HonuaApiClient Api

<PageTitle>Preview: @_layer?.Name</PageTitle>

<div class="container mx-auto p-4">
    <nav class="text-sm mb-4">
        <a href="/layers" class="text-blue-600">Layers</a> /
        <a href="/layers/@LayerId" class="text-blue-600">@_layer?.Name</a> /
        Preview
    </nav>

    @if (_layer is null)
    {
        <p>Loading...</p>
    }
    else
    {
        <div class="bg-white rounded-lg shadow p-4">
            <h1 class="text-xl font-bold mb-4">@_layer.Name</h1>

            <MapPreview
                LayerId="@LayerId"
                Bounds="@_layer.Extent"
                Zoom="10" />

            <div class="mt-4 grid grid-cols-2 gap-4 text-sm">
                <div>
                    <span class="font-medium">Geometry:</span>
                    @_layer.GeometryType
                </div>
                <div>
                    <span class="font-medium">CRS:</span>
                    EPSG:@_layer.Srid
                </div>
                <div>
                    <span class="font-medium">Features:</span>
                    @_layer.FeatureCount?.ToString("N0")
                </div>
                <div>
                    <span class="font-medium">Fields:</span>
                    @_layer.Fields?.Count
                </div>
            </div>
        </div>
    }
</div>

@code {
    [Parameter] public string LayerId { get; set; } = default!;

    private LayerInfo? _layer;

    protected override async Task OnInitializedAsync()
    {
        _layer = await Api.GetLayerAsync(LayerId);
    }
}
```

### File Import Page with Preview

```csharp
// Pages/Import/FileImport.razor
@page "/import/file"
@inject HonuaApiClient Api
@inject IJSRuntime JS

<PageTitle>Import File</PageTitle>

<div class="container mx-auto p-4">
    <h1 class="text-2xl font-bold mb-6">Import File</h1>

    <div class="grid grid-cols-2 gap-6">
        <!-- Upload Panel -->
        <div class="bg-white rounded-lg shadow p-4">
            <h2 class="font-semibold mb-4">1. Select File</h2>

            <FileUpload OnFileSelected="HandleFileSelected"
                        Accept=".geojson,.json,.shp,.zip,.gpkg,.csv,.kml,.kmz"
                        MaxSizeMb="500" />

            @if (_schema is not null)
            {
                <h2 class="font-semibold mt-6 mb-4">2. Configure Import</h2>

                <div class="space-y-4">
                    <div>
                        <label class="block text-sm font-medium">Table Name</label>
                        <input @bind="_tableName" class="mt-1 block w-full rounded border p-2" />
                    </div>

                    <div>
                        <label class="block text-sm font-medium">Source CRS</label>
                        <CrsSelector @bind-Srid="_sourceSrid" DetectedSrid="@_schema.Srid" />
                    </div>

                    <div>
                        <label class="block text-sm font-medium">Target CRS</label>
                        <CrsSelector @bind-Srid="_targetSrid" />
                    </div>

                    <h3 class="font-medium mt-4">Fields (@_schema.Fields.Count)</h3>
                    <div class="max-h-48 overflow-y-auto">
                        <table class="w-full text-sm">
                            <thead>
                                <tr class="border-b">
                                    <th class="text-left py-1">Name</th>
                                    <th class="text-left py-1">Type</th>
                                </tr>
                            </thead>
                            <tbody>
                                @foreach (var field in _schema.Fields)
                                {
                                    <tr class="border-b">
                                        <td class="py-1">@field.Name</td>
                                        <td class="py-1 text-gray-600">@field.InferredType</td>
                                    </tr>
                                }
                            </tbody>
                        </table>
                    </div>

                    <button @onclick="StartImport"
                            disabled="@_importing"
                            class="w-full bg-blue-600 text-white py-2 rounded hover:bg-blue-700 disabled:opacity-50">
                        @(_importing ? "Importing..." : "Import")
                    </button>
                </div>
            }
        </div>

        <!-- Preview Panel -->
        <div class="bg-white rounded-lg shadow p-4">
            <h2 class="font-semibold mb-4">Preview</h2>

            @if (_previewFeatures is not null)
            {
                <MapPreview @ref="_mapPreview" Zoom="4" />

                <div class="mt-4">
                    <h3 class="font-medium">Sample Data (@_previewFeatures.Count features)</h3>
                    <DataGrid Features="_previewFeatures" Fields="_schema?.Fields" />
                </div>
            }
            else
            {
                <div class="h-96 flex items-center justify-center text-gray-500">
                    Upload a file to preview
                </div>
            }
        </div>
    </div>
</div>

@code {
    private InferredSchema? _schema;
    private List<FeaturePreview>? _previewFeatures;
    private string _tableName = "";
    private int _sourceSrid = 4326;
    private int _targetSrid = 4326;
    private bool _importing;
    private MapPreview? _mapPreview;

    private async Task HandleFileSelected(IBrowserFile file)
    {
        // Get schema preview
        using var content = new MultipartFormDataContent();
        using var stream = file.OpenReadStream(500 * 1024 * 1024);
        content.Add(new StreamContent(stream), "file", file.Name);

        var response = await Api.PreviewImportAsync(content);
        _schema = response.Schema;
        _previewFeatures = response.SampleFeatures;
        _tableName = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
        _sourceSrid = _schema.Srid ?? 4326;

        StateHasChanged();
    }

    private async Task StartImport()
    {
        _importing = true;
        try
        {
            var result = await Api.ImportFileAsync(_tableName, _sourceSrid, _targetSrid);
            // Navigate to new layer
        }
        finally
        {
            _importing = false;
        }
    }
}
```

---

## Styling (MVP)

### Core Principle: One Style Per Layer

**MapLibre Style Spec v8 is the canonical format.** One style definition per layer, served in protocol-appropriate format on request.

```
┌─────────────────────────────────────────────────────────────────┐
│  SINGLE STYLE SOURCE OF TRUTH                                   │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Layer                                                          │
│  └── maplibre_style (JSONB) ← canonical                         │
│  └── geoservices_drawing_info (JSONB) ← cached conversion OR import    │
│                                                                 │
│  Protocol requests:                                             │
│  ──────────────────                                             │
│  FeatureServer → Return geoservices_drawing_info (convert if stale)    │
│  OGC API       → Link to /api/styles/{id}.json                  │
│  MVT/TileJSON  → Embed maplibre_style                           │
│  Admin preview → Use maplibre_style directly                    │
│                                                                 │
│  Edit in Maputnik → Update maplibre_style → Invalidate cache    │
│  Import from GeoServices → Store both, maplibre is derived      │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

**Why MapLibre as canonical:**
- Most expressive (data-driven expressions, filters, interpolation)
- Native for web preview (MapLibre GL JS)
- Native for OGC API Styles
- Editable in Maputnik/QGIS/GeoStyler
- JSON, not XML

**GeoServices REST format compatibility:**
- Convert MapLibre → GeoServices drawingInfo on first request, cache it
- When importing from GeoServices server, store original AND derive MapLibre
- Cache invalidated when style is edited

### GeoServices REST Renderer Support

| Renderer | MVP | MapLibre Mapping |
|----------|-----|------------------|
| **SimpleRenderer** | ✅ | Single paint properties |
| **UniqueValueRenderer** | ✅ | `match` expression |
| **ClassBreaksRenderer** | ✅ | `step` expression |
| **HeatmapRenderer** | ❌ | Deferred |
| **DotDensityRenderer** | ❌ | Deferred |
| **TemporalRenderer** | ❌ | Deferred |
| **PictureMarkerSymbol** | ❌ | Requires sprite sheet, deferred |
| **PictureFillSymbol** | ❌ | Deferred |

### Storage Strategy

```sql
-- Layer styling columns
ALTER TABLE honua.layers ADD COLUMN maplibre_style JSONB;      -- Canonical
ALTER TABLE honua.layers ADD COLUMN geoservices_drawing_info JSONB;   -- Cache/import
ALTER TABLE honua.layers ADD COLUMN style_version INT DEFAULT 1;
```

**On style edit (Maputnik, API):**
1. Update `maplibre_style`
2. Increment `style_version`
3. Set `geoservices_drawing_info = NULL` (invalidate cache)

**On FeatureServer request:**
1. If `geoservices_drawing_info` is NULL → convert from `maplibre_style`, cache it
2. Return cached `geoservices_drawing_info`

**On GeoServices REST import:**
1. Store original in `geoservices_drawing_info`
2. Convert to MapLibre, store in `maplibre_style`
3. MapLibre becomes canonical for future edits

### Example Style Data

```jsonc
// maplibre_style (canonical) - simple fill
{
  "version": 8,
  "layers": [{
    "id": "parcels-fill",
    "type": "fill",
    "source": "parcels",
    "paint": {
      "fill-color": "#008080",
      "fill-opacity": 0.7
    }
  }, {
    "id": "parcels-outline",
    "type": "line",
    "source": "parcels",
    "paint": {
      "line-color": "#004040",
      "line-width": 1
    }
  }]
}

// geoservices_drawing_info (cached conversion)
{
  "renderer": {
    "type": "simple",
    "symbol": {
      "type": "esriSFS",
      "style": "esriSFSSolid",
      "color": [0, 128, 128, 180],
      "outline": { "color": [0, 64, 64, 255], "width": 1 }
    }
  }
}
```

### Style Converters (Bidirectional)

#### GeoServices → MapLibre (for import and preview)

```csharp
// Features/Styling/GeoServicesToMapLibreConverter.cs
namespace Honua.Server.Features.Styling;

public static class GeoServicesToMapLibreConverter
{
    public static MapLibreStyle Convert(JsonElement drawingInfo, string layerId, GeometryType geomType)
    {
        var renderer = drawingInfo.GetProperty("renderer");
        var rendererType = renderer.GetProperty("type").GetString();

        return rendererType switch
        {
            "simple" => ConvertSimpleRenderer(renderer, layerId, geomType),
            "uniqueValue" => ConvertUniqueValueRenderer(renderer, layerId, geomType),
            "classBreaks" => ConvertClassBreaksRenderer(renderer, layerId, geomType),
            _ => GetDefaultStyle(layerId, geomType)
        };
    }

    private static MapLibreStyle ConvertSimpleRenderer(
        JsonElement renderer, string layerId, GeometryType geomType)
    {
        var symbol = renderer.GetProperty("symbol");
        var layers = new List<MapLibreLayer>();

        if (geomType is GeometryType.Polygon or GeometryType.MultiPolygon)
        {
            var color = ParseGeoServicesColor(symbol.GetProperty("color"));
            var outline = symbol.TryGetProperty("outline", out var o) ? o : default;

            layers.Add(new MapLibreLayer
            {
                Id = $"{layerId}-fill",
                Type = "fill",
                Source = layerId,
                SourceLayer = layerId,
                Paint = new Dictionary<string, object>
                {
                    ["fill-color"] = color.Rgb,
                    ["fill-opacity"] = color.Alpha
                }
            });

            if (outline.ValueKind != JsonValueKind.Undefined)
            {
                var outlineColor = ParseGeoServicesColor(outline.GetProperty("color"));
                var width = outline.TryGetProperty("width", out var w) ? w.GetDouble() : 1;

                layers.Add(new MapLibreLayer
                {
                    Id = $"{layerId}-outline",
                    Type = "line",
                    Source = layerId,
                    SourceLayer = layerId,
                    Paint = new Dictionary<string, object>
                    {
                        ["line-color"] = outlineColor.Rgb,
                        ["line-width"] = width,
                        ["line-opacity"] = outlineColor.Alpha
                    }
                });
            }
        }
        else if (geomType is GeometryType.LineString or GeometryType.MultiLineString)
        {
            var color = ParseGeoServicesColor(symbol.GetProperty("color"));
            var width = symbol.TryGetProperty("width", out var w) ? w.GetDouble() : 2;

            layers.Add(new MapLibreLayer
            {
                Id = $"{layerId}-line",
                Type = "line",
                Source = layerId,
                SourceLayer = layerId,
                Paint = new Dictionary<string, object>
                {
                    ["line-color"] = color.Rgb,
                    ["line-width"] = width,
                    ["line-opacity"] = color.Alpha
                }
            });
        }
        else // Point
        {
            var color = ParseGeoServicesColor(symbol.GetProperty("color"));
            var size = symbol.TryGetProperty("size", out var s) ? s.GetDouble() : 8;

            layers.Add(new MapLibreLayer
            {
                Id = $"{layerId}-circle",
                Type = "circle",
                Source = layerId,
                SourceLayer = layerId,
                Paint = new Dictionary<string, object>
                {
                    ["circle-color"] = color.Rgb,
                    ["circle-radius"] = size / 2,
                    ["circle-opacity"] = color.Alpha
                }
            });
        }

        return new MapLibreStyle { Layers = layers };
    }

    private static MapLibreStyle ConvertUniqueValueRenderer(
        JsonElement renderer, string layerId, GeometryType geomType)
    {
        var field = renderer.GetProperty("field1").GetString()!;
        var uniqueValues = renderer.GetProperty("uniqueValueInfos").EnumerateArray().ToList();
        var defaultSymbol = renderer.TryGetProperty("defaultSymbol", out var ds) ? ds : default;

        // Build match expression: ["match", ["get", "field"], val1, color1, val2, color2, ..., defaultColor]
        var matchExpr = new List<object> { "match", new object[] { "get", field } };

        foreach (var uv in uniqueValues)
        {
            var value = uv.GetProperty("value").GetString();
            var color = ParseGeoServicesColor(uv.GetProperty("symbol").GetProperty("color"));
            matchExpr.Add(value);
            matchExpr.Add(color.Rgb);
        }

        // Default color
        var defaultColor = defaultSymbol.ValueKind != JsonValueKind.Undefined
            ? ParseGeoServicesColor(defaultSymbol.GetProperty("color")).Rgb
            : "#888888";
        matchExpr.Add(defaultColor);

        var layers = new List<MapLibreLayer>();

        if (geomType is GeometryType.Polygon or GeometryType.MultiPolygon)
        {
            layers.Add(new MapLibreLayer
            {
                Id = $"{layerId}-fill",
                Type = "fill",
                Source = layerId,
                SourceLayer = layerId,
                Paint = new Dictionary<string, object>
                {
                    ["fill-color"] = matchExpr,
                    ["fill-opacity"] = 0.7
                }
            });
        }
        // ... similar for line/point

        return new MapLibreStyle { Layers = layers };
    }

    private static MapLibreStyle ConvertClassBreaksRenderer(
        JsonElement renderer, string layerId, GeometryType geomType)
    {
        var field = renderer.GetProperty("field").GetString()!;
        var classBreaks = renderer.GetProperty("classBreakInfos").EnumerateArray().ToList();

        // Build step expression: ["step", ["get", "field"], color0, break1, color1, break2, color2, ...]
        var stepExpr = new List<object> { "step", new object[] { "get", field } };

        // First color (below first break)
        var firstColor = ParseGeoServicesColor(classBreaks[0].GetProperty("symbol").GetProperty("color"));
        stepExpr.Add(firstColor.Rgb);

        foreach (var cb in classBreaks)
        {
            var maxValue = cb.GetProperty("classMaxValue").GetDouble();
            var color = ParseGeoServicesColor(cb.GetProperty("symbol").GetProperty("color"));
            stepExpr.Add(maxValue);
            stepExpr.Add(color.Rgb);
        }

        var layers = new List<MapLibreLayer>();

        if (geomType is GeometryType.Polygon or GeometryType.MultiPolygon)
        {
            layers.Add(new MapLibreLayer
            {
                Id = $"{layerId}-fill",
                Type = "fill",
                Source = layerId,
                SourceLayer = layerId,
                Paint = new Dictionary<string, object>
                {
                    ["fill-color"] = stepExpr,
                    ["fill-opacity"] = 0.7
                }
            });
        }
        // ... similar for line/point

        return new MapLibreStyle { Layers = layers };
    }

    private static (string Rgb, double Alpha) ParseGeoServicesColor(JsonElement color)
    {
        // GeoServices color: [r, g, b, a] where a is 0-255
        var arr = color.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var rgb = $"rgb({arr[0]}, {arr[1]}, {arr[2]})";
        var alpha = arr.Length > 3 ? arr[3] / 255.0 : 1.0;
        return (rgb, alpha);
    }

    private static MapLibreStyle GetDefaultStyle(string layerId, GeometryType geomType)
    {
        // Sensible defaults when no renderer defined
        return geomType switch
        {
            GeometryType.Polygon or GeometryType.MultiPolygon => new MapLibreStyle
            {
                Layers = new List<MapLibreLayer>
                {
                    new() { Id = $"{layerId}-fill", Type = "fill", Source = layerId, SourceLayer = layerId,
                        Paint = new() { ["fill-color"] = "#088", ["fill-opacity"] = 0.5 } },
                    new() { Id = $"{layerId}-outline", Type = "line", Source = layerId, SourceLayer = layerId,
                        Paint = new() { ["line-color"] = "#044", ["line-width"] = 1 } }
                }
            },
            GeometryType.LineString or GeometryType.MultiLineString => new MapLibreStyle
            {
                Layers = new List<MapLibreLayer>
                {
                    new() { Id = $"{layerId}-line", Type = "line", Source = layerId, SourceLayer = layerId,
                        Paint = new() { ["line-color"] = "#088", ["line-width"] = 2 } }
                }
            },
            _ => new MapLibreStyle
            {
                Layers = new List<MapLibreLayer>
                {
                    new() { Id = $"{layerId}-circle", Type = "circle", Source = layerId, SourceLayer = layerId,
                        Paint = new() { ["circle-color"] = "#088", ["circle-radius"] = 6 } }
                }
            }
        };
    }
}

public class MapLibreStyle
{
    public List<MapLibreLayer> Layers { get; init; } = new();
}

public class MapLibreLayer
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Source { get; init; }
    public required string SourceLayer { get; init; }
    public Dictionary<string, object> Paint { get; init; } = new();
    public Dictionary<string, object>? Layout { get; init; }
}
```

#### MapLibre → GeoServices (for FeatureServer responses)

Converts MapLibre style back to GeoServices drawingInfo for FeatureServer responses.

```csharp
// Features/Styling/MapLibreToGeoServicesConverter.cs
namespace Honua.Server.Features.Styling;

public static class MapLibreToGeoServicesConverter
{
    public static JsonElement Convert(MapLibreStyle style, GeometryType geomType)
    {
        // Find the primary layer (fill for polygon, line for line, circle for point)
        var primaryLayer = FindPrimaryLayer(style, geomType);
        if (primaryLayer is null)
            return GetDefaultGeoServicesRenderer(geomType);

        var paint = primaryLayer.Paint;

        // Check if it's a simple style (no expressions) or data-driven
        if (IsSimpleStyle(paint))
            return ConvertToSimpleRenderer(paint, geomType);

        // Check for match expressions (UniqueValue)
        if (HasMatchExpression(paint))
            return ConvertToUniqueValueRenderer(paint, geomType);

        // Check for step expressions (ClassBreaks)
        if (HasStepExpression(paint))
            return ConvertToClassBreaksRenderer(paint, geomType);

        return GetDefaultGeoServicesRenderer(geomType);
    }

    private static JsonElement ConvertToSimpleRenderer(
        Dictionary<string, object> paint, GeometryType geomType)
    {
        var symbol = geomType switch
        {
            GeometryType.Polygon or GeometryType.MultiPolygon => new
            {
                type = "esriSFS",
                style = "esriSFSSolid",
                color = ParseMapLibreColor(paint.GetValueOrDefault("fill-color", "#888888"),
                                           paint.GetValueOrDefault("fill-opacity", 0.7)),
                outline = new
                {
                    type = "esriSLS",
                    style = "esriSLSSolid",
                    color = ParseMapLibreColor(paint.GetValueOrDefault("line-color", "#444444"), 1.0),
                    width = Convert.ToDouble(paint.GetValueOrDefault("line-width", 1))
                }
            },
            GeometryType.LineString or GeometryType.MultiLineString => (object)new
            {
                type = "esriSLS",
                style = "esriSLSSolid",
                color = ParseMapLibreColor(paint.GetValueOrDefault("line-color", "#888888"),
                                           paint.GetValueOrDefault("line-opacity", 1.0)),
                width = Convert.ToDouble(paint.GetValueOrDefault("line-width", 2))
            },
            _ => new
            {
                type = "esriSMS",
                style = "esriSMSCircle",
                color = ParseMapLibreColor(paint.GetValueOrDefault("circle-color", "#888888"),
                                           paint.GetValueOrDefault("circle-opacity", 1.0)),
                size = Convert.ToDouble(paint.GetValueOrDefault("circle-radius", 6)) * 2
            }
        };

        return JsonSerializer.SerializeToElement(new
        {
            renderer = new { type = "simple", symbol }
        });
    }

    private static int[] ParseMapLibreColor(object colorValue, object opacityValue)
    {
        // Handle hex, rgb(), rgba(), named colors
        var color = colorValue?.ToString() ?? "#888888";
        var opacity = Convert.ToDouble(opacityValue);

        if (color.StartsWith('#'))
        {
            var r = Convert.ToInt32(color.Substring(1, 2), 16);
            var g = Convert.ToInt32(color.Substring(3, 2), 16);
            var b = Convert.ToInt32(color.Substring(5, 2), 16);
            return [r, g, b, (int)(opacity * 255)];
        }
        // ... handle rgb(), rgba() formats

        return [136, 136, 136, (int)(opacity * 255)]; // fallback gray
    }

    // ... ConvertToUniqueValueRenderer, ConvertToClassBreaksRenderer, etc.
}
```

**Conversion limitations:**
- MapLibre expressions more powerful than GeoServices → falls back to simple renderer
- Complex interpolations → step functions
- Custom fonts/icons → defaults

### Style Endpoint

```csharp
// Features/Styling/StyleEndpoint.cs
public static class StyleEndpoint
{
    public static void MapStyleEndpoints(this IEndpointRouteBuilder app)
    {
        // Return MapLibre-compatible style for a layer
        app.MapGet("/api/styles/{layerId}.json", HandleGetStyle);

        // Full map style (all layers)
        app.MapGet("/api/styles/map.json", HandleGetMapStyle);
    }

    private static async Task<IResult> HandleGetStyle(
        string layerId,
        ILayerCatalog catalog,
        CancellationToken ct)
    {
        var layer = await catalog.GetLayerAsync(layerId, ct);
        if (layer is null)
            return Results.NotFound();

        var baseUrl = ""; // Would come from request context

        var style = new
        {
            version = 8,
            name = layer.Name,
            sources = new Dictionary<string, object>
            {
                [layerId] = new
                {
                    type = "vector",
                    tiles = new[] { $"{baseUrl}/rest/services/default/VectorTileServer/{layerId}/tile/{{z}}/{{x}}/{{y}}.pbf" },
                    minzoom = 0,
                    maxzoom = 14
                }
            },
            layers = GetMapLibreLayers(layer)
        };

        return Results.Json(style);
    }

    private static List<object> GetMapLibreLayers(LayerDefinition layer)
    {
        if (layer.DrawingInfo is { } di)
        {
            var mlStyle = GeoServicesToMapLibreConverter.Convert(di, layer.Id, layer.GeometryType!.Value);
            return mlStyle.Layers.Cast<object>().ToList();
        }

        // Default styling
        var defaultStyle = GeoServicesToMapLibreConverter.GetDefaultStyle(layer.Id, layer.GeometryType!.Value);
        return defaultStyle.Layers.Cast<object>().ToList();
    }
}
```

### Style Standards

| Standard | MVP Support | Notes |
|----------|-------------|-------|
| **Mapbox/MapLibre Style Spec v8** | ✅ Primary | Native format for MapLibre GL, industry standard |
| **GeoServices Renderer JSON** | ✅ Storage | Stored verbatim, converted to MapLibre on-the-fly |
| **OGC SLD (Styled Layer Descriptor)** | ❌ | XML-based, legacy — deferred |
| **OGC API Styles** | ❌ | REST API for style management — planned for Beta |
| **CartoCSS** | ❌ | Mapbox legacy — no plans |

**Primary standard is Mapbox/MapLibre Style Specification v8** — the de facto standard for web maps. GeoServices JSON is supported for protocol compatibility, converted server-side.

### Embedded Style Editor (Maputnik)

**[Maputnik](https://github.com/maputnik/editor)** is embedded directly in the admin dashboard for visual style editing. MIT licensed, industry standard.

```
┌─────────────────────────────────────────────────────────────────┐
│  Admin Dashboard — Layer Style Editor                           │
├─────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────────────────────┐   │
│  │  [Layer: parcels ▼]              [Save] [Reset] [Help]  │   │
│  ├─────────────────────────────────────────────────────────┤   │
│  │                                                         │   │
│  │   ┌─────────────────────────────────────────────────┐   │   │
│  │   │                                                 │   │   │
│  │   │           Maputnik (iframe)                     │   │   │
│  │   │                                                 │   │   │
│  │   │   - Layer list                                  │   │   │
│  │   │   - Paint/layout properties                     │   │   │
│  │   │   - Data-driven expressions                     │   │   │
│  │   │   - Live preview on map                         │   │   │
│  │   │                                                 │   │   │
│  │   └─────────────────────────────────────────────────┘   │   │
│  │                                                         │   │
│  └─────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

#### Self-Hosting Maputnik

```dockerfile
# Include Maputnik in the Docker image
FROM node:20-alpine AS maputnik-build
WORKDIR /maputnik
RUN npm install -g @maputnik/editor
RUN cp -r /usr/local/lib/node_modules/@maputnik/editor/dist /maputnik/dist

# In final image
COPY --from=maputnik-build /maputnik/dist /app/wwwroot/maputnik
```

Or download release:
```bash
# scripts/fetch-maputnik.sh
curl -L https://github.com/maputnik/editor/releases/latest/download/dist.zip -o maputnik.zip
unzip maputnik.zip -d src/Honua.Admin/wwwroot/maputnik
```

#### Blazor Integration via postMessage

```csharp
// Admin/Pages/StyleEditor.razor
@page "/admin/layers/{LayerId}/style"
@inject IStyleService StyleService
@inject IJSRuntime JS

<div class="style-editor">
    <div class="toolbar">
        <button @onclick="SaveStyle" disabled="@(!_hasChanges)">Save</button>
        <button @onclick="ResetStyle">Reset</button>
    </div>

    <iframe @ref="_maputnikFrame"
            src="/maputnik/index.html"
            class="maputnik-frame"
            @onload="OnMaputnikLoaded" />
</div>

@code {
    [Parameter] public string LayerId { get; set; } = "";

    private ElementReference _maputnikFrame;
    private MapLibreStyle? _originalStyle;
    private bool _hasChanges;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _originalStyle = await StyleService.GetStyleAsync(LayerId);
            await JS.InvokeVoidAsync("maputnikBridge.init", _maputnikFrame, DotNetObjectReference.Create(this));
        }
    }

    private async Task OnMaputnikLoaded()
    {
        // Send initial style to Maputnik
        if (_originalStyle is not null)
            await JS.InvokeVoidAsync("maputnikBridge.setStyle", _originalStyle);
    }

    [JSInvokable]
    public void OnStyleChanged(JsonElement newStyle)
    {
        _hasChanges = true;
        StateHasChanged();
    }

    private async Task SaveStyle()
    {
        var style = await JS.InvokeAsync<JsonElement>("maputnikBridge.getStyle");
        await StyleService.SaveStyleAsync(LayerId, style);
        _originalStyle = JsonSerializer.Deserialize<MapLibreStyle>(style);
        _hasChanges = false;
    }

    private async Task ResetStyle()
    {
        if (_originalStyle is not null)
            await JS.InvokeVoidAsync("maputnikBridge.setStyle", _originalStyle);
        _hasChanges = false;
    }
}
```

```javascript
// wwwroot/js/maputnik-bridge.js
window.maputnikBridge = {
    frame: null,
    dotnetRef: null,

    init: function(frameElement, dotnetReference) {
        this.frame = frameElement;
        this.dotnetRef = dotnetReference;

        // Listen for style changes from Maputnik
        window.addEventListener('message', (event) => {
            if (event.source !== this.frame.contentWindow) return;

            if (event.data.type === 'style-changed') {
                this.dotnetRef.invokeMethodAsync('OnStyleChanged', event.data.style);
            }
        });
    },

    setStyle: function(style) {
        // Maputnik accepts style via postMessage
        this.frame.contentWindow.postMessage({
            type: 'set-style',
            style: style
        }, '*');
    },

    getStyle: function() {
        return new Promise((resolve) => {
            const handler = (event) => {
                if (event.data.type === 'current-style') {
                    window.removeEventListener('message', handler);
                    resolve(event.data.style);
                }
            };
            window.addEventListener('message', handler);
            this.frame.contentWindow.postMessage({ type: 'get-style' }, '*');
        });
    }
};
```

#### Maputnik Configuration

Configure Maputnik to work with Honua's tile sources:

```javascript
// wwwroot/maputnik/config.json (custom config)
{
  "sources": {
    "honua": {
      "type": "vector",
      "tiles": ["/rest/services/default/VectorTileServer/{layer}/tile/{z}/{x}/{y}.pbf"]
    }
  },
  "sprites": "/api/sprites",
  "glyphs": "/api/fonts/{fontstack}/{range}.pbf"
}
```

### Alternative: External Editors

For users who prefer external tools:

| Tool | Use Case |
|------|----------|
| **[Maputnik Online](https://maputnik.github.io/)** | Quick edits without local setup |
| **[GeoStyler](https://geostyler.org/)** | Convert between SLD/MapLibre/QGIS formats |
| **QGIS** | Export existing styles to MapLibre JSON |

### Style Import/Export Endpoints

```csharp
// Features/Styling/StyleEndpoint.cs (additional endpoints)

// Import MapLibre style
app.MapPut("/api/styles/{layerId}", async (
    string layerId,
    JsonElement maplibreStyle,
    IStyleService styleService,
    CancellationToken ct) =>
{
    // Validate it's a valid MapLibre style
    if (!maplibreStyle.TryGetProperty("version", out var v) || v.GetInt32() != 8)
        return Results.BadRequest("Invalid MapLibre style: version must be 8");

    // Store as MapLibre JSON (separate from GeoServices renderer)
    await styleService.SaveMapLibreStyleAsync(layerId, maplibreStyle, ct);

    return Results.NoContent();
});

// Export style in different formats
app.MapGet("/api/styles/{layerId}", async (
    string layerId,
    [FromQuery] string format, // "maplibre" (default), "geoservices"
    IStyleService styleService,
    CancellationToken ct) =>
{
    var style = await styleService.GetStyleAsync(layerId, ct);
    if (style is null) return Results.NotFound();

    return format?.ToLower() switch
    {
        "geoservices" => Results.Json(style.GeoServicesDrawingInfo),
        _ => Results.Json(style.MapLibreStyle)
    };
});
```

### What's Deferred

| Feature | Why Deferred | Planned |
|---------|--------------|---------|
| **OGC API Styles** | Full REST style management API | Beta |
| **PictureMarkerSymbol** | Requires sprite sheet generation | Beta |
| **PictureFillSymbol** | Pattern fills need image tiles | Beta |
| **HeatmapRenderer** | Different visualization paradigm | GA |
| **DotDensityRenderer** | Complex point generation | GA |
| **Label expressions** | Arcade/SQL expression parsing | Beta |
| **3D symbols** | MapLibre GL doesn't support | Later |
| **OGC SLD import** | Legacy format, low priority | Later |

### MVP Workflow

1. **Import from GeoServices REST**: Renderer JSON preserved, converted to MapLibre (canonical)
2. **New layer (no style)**: Apply sensible defaults by geometry type
3. **Edit style**: Open embedded Maputnik, edit visually, save
4. **FeatureServer response**: Return cached GeoServices JSON (convert from MapLibre if stale)
5. **OGC/MVT response**: Return MapLibre style directly
6. **Admin preview**: MapLibre GL map with live style

---

## Basemaps (MVP)

### Default: OpenFreeMap (No Key Required)

MVP ships with **OpenFreeMap** vector tiles — free, no API key, high quality.

```csharp
// Features/Admin/BasemapService.cs
public class BasemapService
{
    private readonly HonuaOptions _options;

    public const string OpenFreeMapStyle = "https://tiles.openfreemap.org/styles/liberty/style.json";

    public string GetBasemapStyleUrl()
    {
        if (_options.Basemap?.Provider?.ToLower() == "maptiler"
            && !string.IsNullOrEmpty(_options.Basemap.ApiKey))
        {
            return $"https://api.maptiler.com/maps/streets-v2/style.json?key={_options.Basemap.ApiKey}";
        }

        return OpenFreeMapStyle; // Default — free, no key
    }
}
```

### MapLibre Integration

```javascript
// wwwroot/js/maplibre-interop.js
window.maplibreInterop = {
    createMap: function(containerId, basemapStyleUrl) {
        const map = new maplibregl.Map({
            container: containerId,
            style: basemapStyleUrl || 'https://tiles.openfreemap.org/styles/liberty/style.json',
            center: [0, 0],
            zoom: 2
        });
        this.maps[containerId] = map;
        return true;
    },
    // ... rest of interop
};
```

### Optional: MapTiler

For users who want MapTiler (e.g., satellite imagery, custom styles):

```jsonc
// appsettings.json
{
  "Honua": {
    "Basemap": {
      "Provider": "maptiler",
      "ApiKey": "your-maptiler-key"  // Free tier: 100k tiles/mo
    }
  }
}
```

### MVP Basemap Summary

| Provider | API Key | Default | Notes |
|----------|---------|---------|-------|
| **OpenFreeMap** | ❌ None | ✅ Yes | Free vector tiles, just works |
| **MapTiler** | ✅ Optional | ❌ | For users who want satellite/custom |

That's it. Two options. OpenFreeMap works out of the box.

---

### Future: Protomaps (Post-MVP)

We may migrate the default basemap to Protomaps after MVP to support self-hosted/offline deployments
and tighter control over tile data. This would require hosting PMTiles plus the associated style,
sprites, and glyphs, so it is intentionally deferred.

---

### API Client

```csharp
// Services/HonuaApiClient.cs
namespace Honua.Admin.Services;

public sealed class HonuaApiClient
{
    private readonly HttpClient _http;

    public HonuaApiClient(HttpClient http) => _http = http;

    public Task<List<LayerInfo>> GetLayersAsync() =>
        _http.GetFromJsonAsync<List<LayerInfo>>("/api/v1/admin/layers")
            ?? Task.FromResult(new List<LayerInfo>());

    public Task<LayerInfo?> GetLayerAsync(string layerId) =>
        _http.GetFromJsonAsync<LayerInfo>($"/api/v1/admin/layers/{layerId}");

    public Task<ImportPreviewResponse> PreviewImportAsync(MultipartFormDataContent content) =>
        _http.PostAsync("/api/v1/admin/import/preview", content)
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<ImportPreviewResponse>())
            .Unwrap()!;

    public Task<ImportResult> ImportFileAsync(string table, int sourceSrid, int targetSrid) =>
        _http.PostAsJsonAsync("/api/v1/admin/import", new { table, sourceSrid, targetSrid })
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<ImportResult>())
            .Unwrap()!;
}

public record LayerInfo(
    string Id,
    string Name,
    string? GeometryType,
    int Srid,
    long? FeatureCount,
    double[]? Extent,
    List<FieldInfo>? Fields);

public record FieldInfo(string Name, string Type);
public record ImportPreviewResponse(InferredSchema Schema, List<FeaturePreview> SampleFeatures);
public record ImportResult(long FeatureCount, string Message);
```

---

## Testing Strategy

### Testing Pyramid (MVP)

```
┌─────────────────────────────────────────────────────────────────┐
│  OGC CITE Conformance (E2E)                                     │
│  - OGC API Features 1.0 conformance suite                       │
│  - Docker-based TeamEngine runner                               │
│  - ~5% of tests, run nightly or pre-release                     │
└─────────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────────┐
│  Protocol Conformance (Integration)                             │
│  - FeatureServer query/edit response format validation          │
│  - GeoServices JSON structure assertions                               │
│  - ~15% of tests                                                │
└─────────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────────┐
│  Integration Tests (PRIMARY)                                    │
│  - Real PostgreSQL via Testcontainers                           │
│  - Full endpoint → database → response                          │
│  - ~60% of tests                                                │
└─────────────────────────────────────────────────────────────────┘
┌─────────────────────────────────────────────────────────────────┐
│  Unit Tests                                                     │
│  - Query parsing, validators, geometry, tile math               │
│  - Pure functions, no I/O                                       │
│  - ~20% of tests                                                │
└─────────────────────────────────────────────────────────────────┘
```

---

### OGC CITE Conformance (Port from Existing)

**What to port:**
- `ConformanceTestBase.cs` — base class for CITE tests
- `run-ogcapi-features-conformance.sh` — runner script
- `scripts/lib/cite-common.sh` — common shell functions

```csharp
// Tests/Conformance/ConformanceTestBase.cs (PORT)
public abstract class ConformanceTestBase
{
    protected abstract string SuiteName { get; }
    protected abstract string RunnerEnvVar { get; }
    protected abstract string InputEnvVar { get; }
    protected abstract string DefaultArtifactsPath { get; }

    protected async Task RunConformanceTestAsync()
    {
        var runnerPath = Environment.GetEnvironmentVariable(RunnerEnvVar);
        var inputValue = Environment.GetEnvironmentVariable(InputEnvVar);

        // Skip if not configured (opt-in test)
        if (string.IsNullOrWhiteSpace(runnerPath) || string.IsNullOrWhiteSpace(inputValue))
            return;

        var psi = new ProcessStartInfo
        {
            FileName = runnerPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(inputValue);
        psi.ArgumentList.Add(DefaultArtifactsPath);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new XunitException($"{SuiteName} conformance failed.\n{stdout}\n{stderr}");
    }
}

// Tests/Conformance/OgcApiFeaturesConformanceTests.cs
public class OgcApiFeaturesConformanceTests : ConformanceTestBase
{
    protected override string SuiteName => "OGC API Features";
    protected override string RunnerEnvVar => "OGCAPI_FEATURES_RUNNER";
    protected override string InputEnvVar => "OGCAPI_FEATURES_URL";
    protected override string DefaultArtifactsPath => "tests/conformance/ogcapi-features";

    [Fact]
    [Trait("Category", "Conformance")]
    public async Task OgcApiFeatures_Core_PassesConformance() =>
        await RunConformanceTestAsync();
}
```

#### Runner Script (Port)

```bash
#!/usr/bin/env bash
# scripts/run-ogcapi-features-conformance.sh
set -euo pipefail

SERVICE_URL="${1:?Usage: $0 <service_url> [artifacts_dir]}"
ARTIFACTS_DIR="${2:-tests/conformance/ogcapi-features}"
mkdir -p "${ARTIFACTS_DIR}"

ETS_IMAGE="${OGCAPI_FEATURES_ETS_IMAGE:-ogccite/ets-ogcapi-features10:1.7}"
CONTAINER_NAME="cite-features-$$"

# Start TeamEngine
docker run -d --rm --name "${CONTAINER_NAME}" \
  -p 8081:8080 "${ETS_IMAGE}"

# Wait for TeamEngine ready
for i in {1..60}; do
  curl -sf "http://localhost:8081/teamengine" > /dev/null && break
  sleep 2
done

# Run conformance suite
curl -sf "http://localhost:8081/teamengine/rest/suites/ogcapi-features-1.0/run" \
  -u ogctest:ogctest \
  -d "iut=${SERVICE_URL}" \
  -d "ics=Core,GeoJSON,OpenAPI" \
  -o "${ARTIFACTS_DIR}/response.xml"

# Cleanup
docker stop "${CONTAINER_NAME}" || true

# Check pass/fail
if grep -q 'failed="0"' "${ARTIFACTS_DIR}/response.xml"; then
  echo "✓ OGC API Features conformance PASSED"
  exit 0
else
  echo "✗ OGC API Features conformance FAILED"
  cat "${ARTIFACTS_DIR}/response.xml"
  exit 1
fi
```

#### Running Conformance Tests

```bash
# 1. Start Honua server with test data
docker compose up -d

# 2. Seed test data
psql -h localhost -U postgres -d honua -f tests/data/conformance-seed.sql

# 3. Run conformance via dotnet test
OGCAPI_FEATURES_RUNNER=./scripts/run-ogcapi-features-conformance.sh \
OGCAPI_FEATURES_URL=http://localhost:8080/ogc/features \
dotnet test --filter "Category=Conformance"

# Or run script directly
./scripts/run-ogcapi-features-conformance.sh http://localhost:8080/ogc/features
```

#### MVP Conformance Scope

| Suite | In MVP | Docker Image |
|-------|--------|--------------|
| **OGC API Features 1.0** | ✅ | `ogccite/ets-ogcapi-features10:1.7` |
| OGC API Tiles | ❌ | Deferred |
| WFS/WMS/WMTS | ❌ | Legacy, deferred |

---

### Integration Tests (Primary)

```csharp
// Tests/Fixtures/PostgresFixture.cs (PORT + SIMPLIFY)
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgis/postgis:16-3.4")
        .WithDatabase("honua_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        await RunMigrationsAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task SeedAsync(params FeatureRecord[] features)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        foreach (var f in features)
            await conn.ExecuteAsync("INSERT INTO features ...", f);
    }
}
```

```csharp
// Tests/Integration/QueryEndpointTests.cs
public class QueryEndpointTests : IClassFixture<PostgresFixture>
{
    private readonly HttpClient _client;
    private readonly PostgresFixture _db;

    public QueryEndpointTests(PostgresFixture db)
    {
        _db = db;
        var app = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting(
                "ConnectionStrings:DefaultConnection", db.ConnectionString));
        _client = app.CreateClient();
    }

    [Fact]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        await _db.SeedAsync(
            new FeatureRecord { Id = 1, Name = "A", Value = 100 },
            new FeatureRecord { Id = 2, Name = "B", Value = 200 });

        var response = await _client.GetAsync(
            "/rest/services/test/FeatureServer/0/query?where=value>150&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<QueryResponse>();
        result!.Features.Should().ContainSingle()
            .Which.Attributes["name"].Should().Be("B");
    }

    [Fact]
    public async Task Query_WithBbox_ReturnsSpatiallyFilteredFeatures()
    {
        await _db.SeedAsync(
            new FeatureRecord { Id = 1, Geom = "POINT(0 0)" },
            new FeatureRecord { Id = 2, Geom = "POINT(100 100)" });

        var response = await _client.GetAsync(
            "/rest/services/test/FeatureServer/0/query?geometry=-10,-10,10,10&geometryType=esriGeometryEnvelope&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<QueryResponse>();
        result!.Features.Should().ContainSingle().Which.Id.Should().Be(1);
    }
}
```

---

### Unit Tests

```csharp
// Tests/Unit/QueryParserTests.cs
public class QueryParserTests
{
    [Theory]
    [InlineData("population > 1000", "population", ">", "1000")]
    [InlineData("name = 'test'", "name", "=", "'test'")]
    [InlineData("value BETWEEN 1 AND 10", "value", "BETWEEN", "1 AND 10")]
    public void Parse_WhereClause_ExtractsComponents(
        string where, string field, string op, string value)
    {
        var result = WhereClauseParser.Parse(where);

        result.Field.Should().Be(field);
        result.Operator.Should().Be(op);
        result.Value.Should().Be(value);
    }

    [Fact]
    public void Parse_InvalidWhereClause_ThrowsInvalidQueryException()
    {
        var act = () => WhereClauseParser.Parse("DROP TABLE users;--");

        act.Should().Throw<InvalidQueryException>()
            .WithMessage("*dangerous*");
    }
}

// Tests/Unit/TileMathTests.cs
public class TileMathTests
{
    [Theory]
    [InlineData(0, 0, 0, -20037508.34, -20037508.34, 20037508.34, 20037508.34)]
    [InlineData(1, 0, 0, -20037508.34, 0, 0, 20037508.34)]
    [InlineData(2, 0, 0, -20037508.34, 10018754.17, -10018754.17, 20037508.34)]
    public void GetBounds_ReturnsCorrectWebMercatorBounds(
        int z, int x, int y, double xmin, double ymin, double xmax, double ymax)
    {
        var bounds = TileMath.GetBounds(z, x, y);

        bounds.XMin.Should().BeApproximately(xmin, 0.01);
        bounds.YMin.Should().BeApproximately(ymin, 0.01);
        bounds.XMax.Should().BeApproximately(xmax, 0.01);
        bounds.YMax.Should().BeApproximately(ymax, 0.01);
    }
}
```

---

### What to Port vs Write Fresh

| Component | Action | Notes |
|-----------|--------|-------|
| `ConformanceTestBase.cs` | **Port** | OGC CITE test harness |
| `run-ogcapi-features-conformance.sh` | **Port** | Runner script |
| `cite-common.sh` | **Port** | Common shell functions |
| `PostgresFixture.cs` | **Port + Simplify** | Testcontainers setup |
| `TestDataBuilders.cs` | **Port** | Fluent test data creation |
| `OgcAssertions.cs` | **Port** | Custom assertions for OGC responses |
| FeatureServer tests | **Reference** | Rewrite with simplified patterns |
| OGC API Features tests | **Reference** | Rewrite with simplified patterns |
| Unit tests | **Fresh** | New parsers need new tests |

---

### Test Coverage Targets

| Slice | Integration | Unit | Conformance | Total |
|-------|-------------|------|-------------|-------|
| Query | 60% | 25% | OGC Core | 85% |
| Edit | 60% | 20% | OGC Transactions | 80% |
| Attachments | 50% | 20% | - | 70% |
| VectorTiles | 40% | 30% | - | 70% |
| OGC Features | 50% | 20% | OGC CITE | 70% |
| **Overall** | 55% | 23% | 2% | **80%** |

---

## Migration Path

### Phase 1: Foundation

```
Week 1-2
├── Create new repo with structure
├── Set up CI/CD with all gates
├── Implement IFeatureStore interface
├── Implement PostgresFeatureStore (Query only)
├── Create QueryEndpoint + QueryHandler
└── First integration test passing
```

### Phase 2: Query Complete

```
Week 3-4
├── Query with filters
├── Query with spatial predicates
├── Query with pagination
├── Statistics queries
├── GeoJSON + GeoServices JSON output
└── 80%+ coverage on Query slice
```

### Phase 3: Editing

```
Week 5-6
├── Implement EditEndpoint + EditHandler
├── ApplyEdits (add/update/delete)
├── Transaction rollback
├── Editor tracking
└── 80%+ coverage on Edit slice
```

### Phase 4: Attachments + Admin

```
Week 7-10
├── AttachmentEndpoints
├── Blazor Admin UI
├── PostGIS connection UI
├── Layer publishing
└── Import wizard
```

---

## Comparison: Before vs After

| Aspect | Current | Greenfield |
|--------|---------|------------|
| **Controller deps** | 22 | 3-5 per endpoint |
| **Files per feature** | Scattered across 7 partials | 5-6 in one folder |
| **Lines per class** | 500-1000 | 50-200 |
| **Test setup** | Mock 22 things | Mock 2-3 things |
| **Add new endpoint** | Modify controller | Add new folder |
| **Output formats** | 10 | 3 (GeoJSON, GeoServices JSON, MVT) |
| **Database support** | 8+ | 1 (PostgreSQL) |

---

## MVP Protocol Scope

| Protocol / Feature | In MVP | Notes |
|--------------------|--------|-------|
| **FeatureServer** (query) | ✅ | where, geometry, outFields, paging |
| **FeatureServer** (edit) | ✅ | applyEdits, add/update/delete |
| **FeatureServer** (attachments) | ✅ | CRUD for attachments |
| **VectorTileServer** (MVT) | ✅ | PostGIS ST_AsMVT, TileJSON |
| **OGC API Features** (Core) | ✅ | collections, items, bbox, limit |
| **OGC API Features** (Transactions) | ✅ | POST/PUT/DELETE |
| **OData v4** (read-only) | ✅ | $filter/$select + geo.distance/intersects |
| **File Import** | ✅ | GeoJSON, Shapefile, GeoPackage, CSV, KML |
| **CRS/Reprojection** | ✅ | Any EPSG via PostGIS ST_Transform |
| **Styling** | ✅ | Simple/UniqueValue/ClassBreaks → MapLibre |
| **MapServer** (raster export) | ❌ | Deferred to Beta |
| **OGC API Tiles** | ❌ | Deferred (GeoServices-style only) |
| **OGC API Maps** | ❌ | Deferred |
| **WFS/WMS/WMTS** | ❌ | Legacy, deferred |
| **Advanced renderers** | ❌ | Heatmap, DotDensity, PictureSymbols |

---

## Summary

### Key Architectural Decisions

1. **Vertical slices** over horizontal layers
2. **Composition** over inheritance
3. **Minimal APIs** over controllers
4. **Focused interfaces** (max 10 methods)
5. **Focused classes** (max 5 dependencies)
6. **Integration-first testing** with real PostgreSQL
7. **MVP scope** — defer advanced features

### What We're Keeping from Current Codebase

- `IFeatureRepository` interface design (simplified)
- Composition pattern from `PostgresDataStoreProvider`
- Observability patterns (`ExecuteAsync` wrapper)
- Error response helpers
- Query builder approach

### What We're Dropping

- 22-dependency controller
- Partial class explosion
- Deep inheritance hierarchies
- 10 output formats (keep 2)
- Multi-database support (keep 1)
- Advanced features (replicas, offline sync, etc.)

---

## Infrastructure Patterns

### Observability

The current codebase has a solid observability foundation. For the greenfield MVP, we simplify while keeping the core patterns.

#### OpenTelemetry Setup

```csharp
// Infrastructure/Observability/HonuaTelemetry.cs
public static class HonuaTelemetry
{
    public const string ServiceName = "Honua.Server";
    public static readonly string ServiceVersion =
        typeof(HonuaTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    // Single ActivitySource for MVP (expand later if needed)
    public static readonly ActivitySource Source = new(ServiceName, ServiceVersion);

    // Meters for key metrics
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // Pre-defined counters and histograms
    public static readonly Counter<long> RequestCounter =
        Meter.CreateCounter<long>("honua.requests", "request", "Total requests");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("honua.request.duration", "ms", "Request duration");

    public static readonly Counter<long> ErrorCounter =
        Meter.CreateCounter<long>("honua.errors", "error", "Total errors");
}
```

#### Registration in Program.cs

```csharp
// Program.cs
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(HonuaTelemetry.ServiceName, serviceVersion: HonuaTelemetry.ServiceVersion))
    .WithTracing(t => t
        .AddSource(HonuaTelemetry.ServiceName)
        .AddAspNetCoreInstrumentation()
        .AddNpgsql()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter(HonuaTelemetry.ServiceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter());

// Logging to OTLP (for Aspire Dashboard)
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.AddOtlpExporter();
});
```

#### Aspire Dashboard (Standalone)

For local development and lightweight production monitoring, we use the **Aspire Dashboard standalone container**. No full Aspire orchestration needed — just a container that receives OTLP telemetry.

```yaml
# docker/docker-compose.yml
services:
  honua:
    build: .
    ports:
      - "8080:8080"
    environment:
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889
      - OTEL_SERVICE_NAME=Honua.Server
    depends_on:
      - postgres
      - aspire-dashboard

  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:9.0
    ports:
      - "18888:18888"   # Dashboard UI
      - "18889:18889"   # OTLP gRPC receiver
    environment:
      - DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true  # Dev only

  postgres:
    image: postgis/postgis:16-3.4
    # ...
```

**Access:** `http://localhost:18888` — traces, metrics, and logs in one place. No additional infrastructure needed.

#### Tracing Pattern in Handlers

```csharp
// Features/Query/QueryHandler.cs
public async Task<Result<QueryResponse, QueryError>> HandleAsync(
    string serviceId, int layerIndex, QueryRequest request, CancellationToken ct)
{
    using var activity = HonuaTelemetry.Source.StartActivity("Query.Handle");
    activity?.SetTag("service.id", serviceId);
    activity?.SetTag("layer.index", layerIndex);

    var stopwatch = Stopwatch.StartNew();
    try
    {
        // ... handler logic ...

        HonuaTelemetry.RequestCounter.Add(1,
            new KeyValuePair<string, object?>("operation", "query"),
            new KeyValuePair<string, object?>("status", "success"));

        return result;
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        HonuaTelemetry.ErrorCounter.Add(1,
            new KeyValuePair<string, object?>("operation", "query"),
            new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
        throw;
    }
    finally
    {
        HonuaTelemetry.RequestDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", "query"));
    }
}
```

---

### Exception Handling

#### Exception Hierarchy

```csharp
// Core/Exceptions/HonuaException.cs
public abstract class HonuaException : Exception
{
    public string ErrorCode { get; }

    protected HonuaException(string errorCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

// Marker interface for transient (retryable) exceptions
public interface ITransientException { }

// Specific exception types
public sealed class LayerNotFoundException : HonuaException
{
    public LayerNotFoundException(string serviceId, int layerIndex)
        : base("LAYER_NOT_FOUND", $"Layer {layerIndex} not found in service '{serviceId}'")
    {
    }
}

public sealed class InvalidQueryException : HonuaException
{
    public IReadOnlyList<string> ValidationErrors { get; }

    public InvalidQueryException(IReadOnlyList<string> errors)
        : base("INVALID_QUERY", "Query validation failed")
    {
        ValidationErrors = errors;
    }
}

public sealed class DataStoreException : HonuaException, ITransientException
{
    public DataStoreException(string message, Exception? inner = null)
        : base("DATA_STORE_ERROR", message, inner)
    {
    }
}
```

#### RFC 7807 Problem Details

```csharp
// Infrastructure/ErrorHandling/GlobalExceptionHandler.cs
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        var problem = exception switch
        {
            LayerNotFoundException ex => new ProblemDetails
            {
                Type = "https://honua.io/errors/layer-not-found",
                Title = "Layer Not Found",
                Status = StatusCodes.Status404NotFound,
                Detail = ex.Message,
                Extensions = { ["code"] = ex.ErrorCode }
            },
            InvalidQueryException ex => new ProblemDetails
            {
                Type = "https://honua.io/errors/invalid-query",
                Title = "Invalid Query",
                Status = StatusCodes.Status400BadRequest,
                Detail = ex.Message,
                Extensions =
                {
                    ["code"] = ex.ErrorCode,
                    ["errors"] = ex.ValidationErrors
                }
            },
            DataStoreException ex => new ProblemDetails
            {
                Type = "https://honua.io/errors/data-store-error",
                Title = "Data Store Error",
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = _env.IsDevelopment() ? ex.Message : "A data store error occurred"
            },
            _ => new ProblemDetails
            {
                Type = "https://honua.io/errors/internal-error",
                Title = "Internal Server Error",
                Status = StatusCodes.Status500InternalServerError,
                Detail = _env.IsDevelopment() ? exception.Message : "An unexpected error occurred"
            }
        };

        // Log with appropriate level
        if (exception is ITransientException)
            _logger.LogWarning(exception, "Transient error: {Message}", exception.Message);
        else if (problem.Status >= 500)
            _logger.LogError(exception, "Server error: {Message}", exception.Message);
        else
            _logger.LogInformation("Client error: {Message}", exception.Message);

        context.Response.StatusCode = problem.Status ?? 500;
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
```

#### Registration

```csharp
// Program.cs
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// In middleware pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();
```

#### Protocol-Specific Error Formats

Each protocol has its own error response format. The global exception handler detects the protocol from the request path and formats errors accordingly.

**GeoServices REST** — Returns `{ "error": { "code", "message", "details" } }`:

```json
{
  "error": {
    "code": 400,
    "message": "Invalid where clause",
    "details": ["Syntax error near 'AND'"]
  }
}
```

**OGC API Features** — RFC 7807 Problem Details:

```json
{
  "type": "https://honua.io/errors/invalid-query",
  "title": "Invalid Query",
  "status": 400,
  "detail": "Invalid CQL filter expression",
  "instance": "/ogc/features/collections/parks/items"
}
```

**OData v4** — OData Error format:

```json
{
  "error": {
    "code": "InvalidFilter",
    "message": "Invalid $filter expression: unexpected token 'xyz'"
  }
}
```

**Protocol Detection:**

```csharp
private string DetectProtocol(HttpContext context)
{
    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/rest/services")) return "geoservices";
    if (path.StartsWith("/ogc/")) return "ogc";
    if (path.StartsWith("/odata/")) return "odata";
    return "rfc7807"; // Default to Problem Details
}
```

---

### Resilience (Polly v8)

For MVP, we use a simplified resilience setup focusing on database operations.

#### Resilience Pipelines

```csharp
// Infrastructure/Resilience/ResiliencePipelines.cs
public static class ResiliencePipelines
{
    public const string Database = "database";

    public static IServiceCollection AddResiliencePipelines(this IServiceCollection services)
    {
        services.AddResiliencePipeline(Database, builder =>
        {
            builder
                // Timeout for individual operations
                .AddTimeout(TimeSpan.FromSeconds(30))

                // Retry transient failures
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder()
                        .Handle<NpgsqlException>(ex => IsTransient(ex))
                        .Handle<TimeoutException>(),
                    MaxRetryAttempts = 3,
                    Delay = TimeSpan.FromMilliseconds(200),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    OnRetry = args =>
                    {
                        HonuaTelemetry.ErrorCounter.Add(1,
                            new KeyValuePair<string, object?>("type", "retry"),
                            new KeyValuePair<string, object?>("attempt", args.AttemptNumber));
                        return default;
                    }
                })

                // Circuit breaker for cascading failure protection
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder()
                        .Handle<NpgsqlException>()
                        .Handle<TimeoutException>(),
                    FailureRatio = 0.5,
                    MinimumThroughput = 10,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(30),
                    OnOpened = args =>
                    {
                        HonuaTelemetry.ErrorCounter.Add(1,
                            new KeyValuePair<string, object?>("type", "circuit_opened"));
                        return default;
                    }
                });
        });

        return services;
    }

    private static bool IsTransient(NpgsqlException ex)
    {
        // Connection failures, deadlocks, etc.
        return ex.IsTransient || ex.SqlState?.StartsWith("08") == true;
    }
}
```

#### Using Resilience in Data Store

```csharp
// Postgres/PostgresFeatureStore.cs
public sealed class PostgresFeatureStore : IFeatureStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ResiliencePipeline _pipeline;

    public PostgresFeatureStore(
        NpgsqlDataSource dataSource,
        [FromKeyedServices(ResiliencePipelines.Database)] ResiliencePipeline pipeline)
    {
        _dataSource = dataSource;
        _pipeline = pipeline;
    }

    public async Task<QueryResult> QueryAsync(
        string layerId, FeatureQuery query, CancellationToken ct)
    {
        return await _pipeline.ExecuteAsync(async token =>
        {
            await using var conn = await _dataSource.OpenConnectionAsync(token);
            // ... execute query ...
        }, ct);
    }
}
```

---

### Database Migrations

MVP uses a simple embedded migration strategy — SQL scripts bundled in the application, run on startup.

#### Migration Strategy

| Approach | Pros | Cons | Decision |
|----------|------|------|----------|
| EF Core Migrations | Familiar, tooling | Heavy, reflection-based, breaks AOT | ❌ |
| FluentMigrator | Code-based, flexible | Another dependency | ❌ |
| DbUp | Simple, embedded SQL | Well-suited for MVP | ✅ MVP |
| Raw SQL scripts | Zero dependencies | Manual versioning | Fallback |

**Why DbUp:** Lightweight (~10KB), embeds SQL files, tracks versions in a table, works with AOT.

#### Migration Files

```
Honua.Server/
└── Migrations/
    ├── 001_initial_schema.sql       # honua schema, layers, services tables
    ├── 002_attachments.sql          # Attachments table
    ├── 003_styles.sql               # Style columns (maplibre_style, etc.)
    └── 004_audit_log.sql            # Audit logging table
```

#### Migration Runner

```csharp
// Infrastructure/Migrations/MigrationRunner.cs
public static class MigrationRunner
{
    public static void ApplyMigrations(string connectionString)
    {
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(MigrationRunner).Assembly,
                s => s.StartsWith("Honua.Server.Migrations"))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Migration failed: {result.Error.Message}", result.Error);
        }
    }
}
```

#### Startup Integration

```csharp
// Program.cs
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string required");

// Apply migrations before starting the app
MigrationRunner.ApplyMigrations(connectionString);
```

#### Initial Schema (001_initial_schema.sql)

```sql
-- Honua metadata schema
CREATE SCHEMA IF NOT EXISTS honua;

-- Services (FeatureServer instances)
CREATE TABLE honua.services (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    enabled BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Layers
CREATE TABLE honua.layers (
    id TEXT PRIMARY KEY,
    service_id TEXT NOT NULL REFERENCES honua.services(id) ON DELETE CASCADE,
    layer_index INT NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    table_schema TEXT NOT NULL,
    table_name TEXT NOT NULL,
    geometry_field TEXT,
    geometry_type TEXT,
    object_id_field TEXT NOT NULL DEFAULT 'objectid',
    srid INT NOT NULL DEFAULT 4326,
    enabled BOOLEAN NOT NULL DEFAULT true,
    maplibre_style JSONB,
    geoservices_drawing_info JSONB,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(service_id, layer_index)
);

-- Layer fields
CREATE TABLE honua.layer_fields (
    layer_id TEXT NOT NULL REFERENCES honua.layers(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    alias TEXT,
    field_type TEXT NOT NULL,
    nullable BOOLEAN NOT NULL DEFAULT true,
    editable BOOLEAN NOT NULL DEFAULT true,
    visible BOOLEAN NOT NULL DEFAULT true,
    ordinal INT NOT NULL,
    PRIMARY KEY (layer_id, name)
);

-- Connections (PostGIS databases)
CREATE TABLE honua.connections (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    connection_string TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_layers_service_id ON honua.layers(service_id);
```

#### Rollback Strategy

For MVP, rollbacks are manual SQL scripts. Each migration file has a corresponding `xxx_rollback.sql` for emergencies. In production:

1. **Forward-only** — Prefer additive changes (new columns, tables)
2. **Blue-green** — Deploy new version alongside old, cut over
3. **Feature flags** — Gate new functionality during transition

---

### Logging

#### Structured Logging Setup

```csharp
// Program.cs
builder.Logging.ClearProviders();
builder.Logging.AddOpenTelemetry(options =>
{
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.ParseStateValues = true;
    options.AddOtlpExporter();
});

// Development only
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddConsole();
}
```

#### Log Categories

```csharp
// Use typed loggers for clear categorization
public sealed class QueryHandler
{
    private readonly ILogger<QueryHandler> _logger;

    public async Task<Result<QueryResponse, QueryError>> HandleAsync(...)
    {
        _logger.LogInformation(
            "Executing query for service {ServiceId}, layer {LayerIndex}, where: {Where}",
            serviceId, layerIndex, request.Where ?? "(none)");

        // ...

        _logger.LogDebug(
            "Query returned {FeatureCount} features in {ElapsedMs}ms",
            result.Features.Count, stopwatch.ElapsedMilliseconds);
    }
}
```

#### Log Level Configuration

Configure log levels via environment variables for Docker deployments:

```bash
# Set minimum log level
docker run -p 8080:8080 \
  -e Logging__LogLevel__Default="Information" \
  -e Logging__LogLevel__Microsoft.AspNetCore="Warning" \
  -e Logging__LogLevel__Npgsql="Warning" \
  -e Logging__LogLevel__Honua="Debug" \
  ghcr.io/honuaio/honua-server:latest
```

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `Logging__LogLevel__Default` | `Information` | Minimum level for all loggers |
| `Logging__LogLevel__Microsoft.AspNetCore` | `Warning` | ASP.NET Core framework logs |
| `Logging__LogLevel__Npgsql` | `Warning` | PostgreSQL driver logs |
| `Logging__LogLevel__Honua` | `Information` | Application logs |

Log levels: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`.

#### JSON Console Output (Production)

For container log aggregation (ELK, CloudWatch, etc.), use JSON output:

```bash
docker run -p 8080:8080 \
  -e Logging__Console__FormatterName="json" \
  ghcr.io/honuaio/honua-server:latest
```

Produces structured logs like:

```json
{
  "Timestamp": "2025-01-15T10:30:00.123Z",
  "Level": "Information",
  "MessageTemplate": "Executing query for service {ServiceId}",
  "Properties": {
    "ServiceId": "myservice",
    "CorrelationId": "abc123"
  }
}
```

#### Correlation IDs

```csharp
// Infrastructure/Middleware/CorrelationMiddleware.cs
public sealed class CorrelationMiddleware
{
    private readonly RequestDelegate _next;
    private const string CorrelationIdHeader = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Activity.Current?.Id
            ?? Guid.NewGuid().ToString("N");

        context.Response.Headers[CorrelationIdHeader] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        }))
        {
            await _next(context);
        }
    }
}
```

---

### Infrastructure Summary

| Concern | MVP Approach |
|---------|--------------|
| **Tracing** | Single ActivitySource → Aspire Dashboard |
| **Metrics** | Basic counters + histograms → Aspire Dashboard |
| **Logs** | Structured logging → Aspire Dashboard |
| **Resilience** | Polly pipeline for database operations |
| **Exceptions** | HonuaException hierarchy + RFC 7807 Problem Details |

---

## Cross-Cutting Concerns

### Authentication

Simplified admin password authentication for MVP. Use `HONUA_ADMIN_PASSWORD` and pass it via the `X-API-Key` header.

```csharp
// Infrastructure/Auth/ApiKeyAuthenticationHandler.cs
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ApiKeyHeader = "X-API-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out var apiKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        var configuredPassword = Context.RequestServices
            .GetRequiredService<IConfiguration>()["HONUA_ADMIN_PASSWORD"];

        if (string.IsNullOrEmpty(configuredPassword))
            return Task.FromResult(AuthenticateResult.NoResult());

        // Constant-time comparison to prevent timing attacks
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(apiKey!),
            Encoding.UTF8.GetBytes(configuredPassword)))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin password"));
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
```

#### Registration

```csharp
// Program.cs
builder.Services.AddAuthentication("ApiKey")
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKey", null);

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireAuthenticatedUser());
});

// Protect admin endpoints
app.MapGroup("/admin").RequireAuthorization("Admin");
```

---

### Health Checks

Minimal health checks for container orchestration.

```csharp
// Program.cs
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "database", tags: new[] { "ready" })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" });

// Endpoints
app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

---

### Rate Limiting

MVP defers rate limiting to the reverse proxy layer. This is the standard pattern for containerized deployments and avoids adding application complexity.

#### Why Proxy-Based Rate Limiting

| Approach | Pros | Cons | Decision |
|----------|------|------|----------|
| ASP.NET Rate Limiting | Built-in, configurable | Adds complexity, less flexible | ❌ |
| Reverse proxy (nginx/Traefik) | Standard pattern, battle-tested | External dependency | ✅ MVP |
| API Gateway (Kong/YARP) | Rich features | Over-engineered for MVP | Post-MVP |

#### Nginx Example

```nginx
# /etc/nginx/conf.d/honua.conf
upstream honua {
    server honua:8080;
}

# Rate limiting zones
limit_req_zone $binary_remote_addr zone=general:10m rate=100r/s;
limit_req_zone $binary_remote_addr zone=tiles:10m rate=200r/s;
limit_req_zone $binary_remote_addr zone=admin:10m rate=10r/s;

server {
    listen 80;
    server_name api.example.com;

    # General API endpoints
    location /rest/services/ {
        limit_req zone=general burst=20 nodelay;
        proxy_pass http://honua;
    }

    # Tile endpoints (higher limit)
    location ~ /VectorTileServer/tile/ {
        limit_req zone=tiles burst=50 nodelay;
        proxy_pass http://honua;
    }

    # Admin endpoints (stricter limit)
    location /admin/ {
        limit_req zone=admin burst=5 nodelay;
        proxy_pass http://honua;
    }

    # OGC and OData
    location /ogc/ {
        limit_req zone=general burst=20 nodelay;
        proxy_pass http://honua;
    }

    location /odata/ {
        limit_req zone=general burst=20 nodelay;
        proxy_pass http://honua;
    }
}
```

#### Traefik Example (docker-compose)

```yaml
# docker-compose.yml
services:
  traefik:
    image: traefik:v3.0
    command:
      - "--providers.docker=true"
      - "--entrypoints.web.address=:80"
    ports:
      - "80:80"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock

  honua:
    image: ghcr.io/honuaio/honua-server:latest
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.honua.rule=Host(`api.example.com`)"
      - "traefik.http.middlewares.honua-ratelimit.ratelimit.average=100"
      - "traefik.http.middlewares.honua-ratelimit.ratelimit.burst=20"
      - "traefik.http.routers.honua.middlewares=honua-ratelimit"
```

#### Response Headers

When rate limited, proxies typically return:

```
HTTP/1.1 429 Too Many Requests
Retry-After: 1
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 0
X-RateLimit-Reset: 1639123456
```

---

### Input Validation

Manual validation with `IValidatable` interface and endpoint filters (AOT-safe, no reflection).

```csharp
// Core/Validation/IValidatable.cs
public interface IValidatable
{
    (bool IsValid, IDictionary<string, string[]> Errors) Validate();
}

// Features/Query/QueryRequest.cs
public record QueryRequest : IValidatable
{
    public string? Where { get; init; }
    public string? OutFields { get; init; }
    public int? ResultRecordCount { get; init; }
    public int? ResultOffset { get; init; }

    public (bool IsValid, IDictionary<string, string[]> Errors) Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (ResultRecordCount is < 1 or > 10000)
            errors["resultRecordCount"] = ["Must be between 1 and 10000"];

        if (ResultOffset < 0)
            errors["resultOffset"] = ["Must be non-negative"];

        if (OutFields is not null && OutFields != "*" && !IsValidFieldList(OutFields))
            errors["outFields"] = ["Contains invalid field names"];

        return (errors.Count == 0, errors);
    }

    private static bool IsValidFieldList(string fields) =>
        fields.Split(',').All(f => Regex.IsMatch(f.Trim(), @"^[a-zA-Z_]\w*$"));
}
```

#### Endpoint Filter

```csharp
// Infrastructure/Filters/ValidationFilter.cs
public sealed class ValidationFilter<T> : IEndpointFilter where T : IValidatable
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (ctx.Arguments.OfType<T>().FirstOrDefault() is { } request)
        {
            var (isValid, errors) = request.Validate();
            if (!isValid)
                return Results.ValidationProblem(errors);
        }
        return await next(ctx);
    }
}

// Usage
app.MapGet("/query", HandleQuery)
    .AddEndpointFilter<ValidationFilter<QueryRequest>>();
```

---

### SQL Injection Prevention

Defense in depth: parameterized queries + pattern detection.

```csharp
// Core/Security/InputValidation.cs
public static class InputValidation
{
    private static readonly Regex DangerousPatterns = new(
        @"(--|;|'|""|/\*|\*/|xp_|sp_|exec|execute|insert|update|delete|drop|truncate|alter|create|union|select\s+.*\s+from)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Defense-in-depth check. Primary defense is always parameterized queries.
    /// </summary>
    public static bool ContainsSqlInjectionPatterns(string? input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        return DangerousPatterns.IsMatch(input);
    }

    public static void ThrowIfDangerous(string? input, string paramName)
    {
        if (ContainsSqlInjectionPatterns(input))
            throw new ArgumentException($"Input contains dangerous patterns", paramName);
    }
}
```

#### Parameterized Queries with Raw Npgsql

```csharp
// Postgres/PostgresFeatureStore.cs
public async Task<QueryResult> QueryAsync(string layerId, FeatureQuery query, CancellationToken ct)
{
    // NEVER interpolate user input into SQL - always use parameters
    var sql = """
        SELECT * FROM features
        WHERE layer_id = @LayerId
        AND (@Where IS NULL OR name ILIKE @Where)
        LIMIT @Limit OFFSET @Offset
        """;

    await using var conn = await _dataSource.OpenConnectionAsync(ct);
    await using var cmd = new NpgsqlCommand(sql, conn);

    // Parameters are always safe - values are never interpolated into SQL
    cmd.Parameters.AddWithValue("LayerId", layerId);
    cmd.Parameters.AddWithValue("Where", (object?)query.WherePattern ?? DBNull.Value);
    cmd.Parameters.AddWithValue("Limit", query.Limit);
    cmd.Parameters.AddWithValue("Offset", query.Offset);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    var features = new List<FeatureRecord>();

    while (await reader.ReadAsync(ct))
    {
        features.Add(MapFeature(reader));
    }

    return new QueryResult(features);
}
```

---

### CORS

Static CORS configuration for Blazor WASM admin.

```csharp
// Program.cs
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = builder.Configuration
            .GetSection("Cors:AllowedOrigins")
            .Get<string[]>() ?? Array.Empty<string>();

        if (origins.Length > 0)
            policy.WithOrigins(origins);
        else if (builder.Environment.IsDevelopment())
            policy.AllowAnyOrigin();  // Dev only

        policy.AllowAnyMethod()
              .AllowAnyHeader();
    });
});

app.UseCors();
```

---

### Request Size Limits

Prevent DoS via large payloads.

```csharp
// Program.cs
builder.WebHost.ConfigureKestrel(options =>
{
    // Global limit: 50MB (for attachments)
    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024;
});

// Per-endpoint limits
app.MapPost("/applyEdits", handler)
    .WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024));  // 10MB for edits

app.MapPost("/addAttachment", handler)
    .WithMetadata(new RequestSizeLimitAttribute(50 * 1024 * 1024));  // 50MB for attachments
```

---

### Response Compression

Essential for GeoJSON and MVT responses.

```csharp
// Program.cs
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();

    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json",
        "application/geo+json",
        "application/vnd.mapbox-vector-tile",
        "application/x-protobuf"
    });
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;  // Balance speed vs size
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

// Middleware (before other response-modifying middleware)
app.UseResponseCompression();
```

#### Compression Impact

| Response Type | Typical Size | Compressed | Reduction |
|---------------|--------------|------------|-----------|
| GeoJSON (1000 features) | ~500KB | ~50KB | 90% |
| MVT tile | ~100KB | ~20KB | 80% |
| GeoServices JSON | ~400KB | ~40KB | 90% |

#### Static File Compression (MVT Cache)

If caching tiles to disk, pre-compress them:

```csharp
// Serve pre-compressed .pbf.gz files
app.UseStaticFiles(new StaticFileOptions
{
    ServeUnknownFileTypes = true,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".pbf.gz", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.ContentEncoding = "gzip";
            ctx.Context.Response.ContentType = "application/vnd.mapbox-vector-tile";
        }
    }
});
```

---

### Metadata Caching (Cloud-Native)

Layer and service metadata rarely changes but is needed on every request. Use `IDistributedCache` abstraction for cloud-native scaling from day one.

#### Cache Abstraction

Same code, swap implementation via config:

```csharp
// Program.cs
if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redisConn)
{
    // Production / multi-instance: Redis
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConn;
        options.InstanceName = "honua:";
    });
}
else
{
    // Dev / single-instance: in-memory (still uses IDistributedCache interface)
    builder.Services.AddDistributedMemoryCache();
}

builder.Services.AddSingleton<PostgresLayerCatalog>();
builder.Services.AddSingleton<ILayerCatalog, DistributedLayerCatalog>();
```

#### Implementation

```csharp
// Core/Abstractions/ILayerCatalog.cs
public interface ILayerCatalog
{
    Task<LayerDefinition?> GetLayerAsync(string serviceId, int layerIndex, CancellationToken ct);
    Task<IReadOnlyList<LayerDefinition>> GetLayersAsync(string serviceId, CancellationToken ct);
    Task InvalidateCacheAsync(string serviceId, CancellationToken ct);
}

// Postgres/DistributedLayerCatalog.cs
public sealed class DistributedLayerCatalog : ILayerCatalog
{
    private readonly PostgresLayerCatalog _inner;
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedLayerCatalog> _logger;

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    public DistributedLayerCatalog(
        PostgresLayerCatalog inner,
        IDistributedCache cache,
        ILogger<DistributedLayerCatalog> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<LayerDefinition?> GetLayerAsync(
        string serviceId, int layerIndex, CancellationToken ct)
    {
        var key = $"layer:{serviceId}:{layerIndex}";

        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<LayerDefinition>(cached);

        var layer = await _inner.GetLayerAsync(serviceId, layerIndex, ct);
        if (layer is not null)
        {
            await _cache.SetAsync(key,
                JsonSerializer.SerializeToUtf8Bytes(layer),
                CacheOptions, ct);
        }

        return layer;
    }

    public async Task<IReadOnlyList<LayerDefinition>> GetLayersAsync(
        string serviceId, CancellationToken ct)
    {
        var key = $"layers:{serviceId}";

        var cached = await _cache.GetAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<LayerDefinition>>(cached) ?? [];

        var layers = await _inner.GetLayersAsync(serviceId, ct);
        await _cache.SetAsync(key,
            JsonSerializer.SerializeToUtf8Bytes(layers),
            CacheOptions, ct);

        return layers;
    }

    public async Task InvalidateCacheAsync(string serviceId, CancellationToken ct)
    {
        // Works across all instances when using Redis
        await _cache.RemoveAsync($"layers:{serviceId}", ct);

        // For individual layers, track indices in a set or use key pattern
        _logger.LogInformation("Invalidated cache for service {ServiceId}", serviceId);
    }
}
```

#### Cache Invalidation on Admin Updates

```csharp
// Features/Admin/LayerAdminHandler.cs
public async Task<IResult> UpdateLayerAsync(
    string serviceId, int layerIndex, UpdateLayerRequest request, CancellationToken ct)
{
    await _store.UpdateLayerAsync(serviceId, layerIndex, request, ct);

    // Invalidate across all instances (if Redis) or local (if in-memory)
    await _catalog.InvalidateCacheAsync(serviceId, ct);

    return Results.Ok();
}
```

#### Docker Compose (Redis Optional)

```yaml
# docker/docker-compose.yml
services:
  honua:
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=honua;...
      - ConnectionStrings__Redis=redis:6379  # Remove line for single-instance
    depends_on:
      - postgres
      - redis

  postgres:
    image: postgis/postgis:16-3.4
    # ...

  redis:
    image: redis:7-alpine
    ports:
      - "6379:6379"
    # Remove entire service for single-instance deployments

  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:9.0
    # ...
```

#### Deployment Modes

| Mode | Redis Config | Behavior |
|------|--------------|----------|
| **Dev / Single** | `ConnectionStrings__Redis` not set | In-memory, no extra container |
| **Prod / Multi** | `ConnectionStrings__Redis=redis:6379` | Distributed, scales horizontally |

Same code, same interface. Cloud-native ready from day one.

#### What to Cache

| Data | Cache? | TTL | Invalidation |
|------|--------|-----|--------------|
| Layer definitions | ✅ | 5 min | On admin update |
| Service metadata | ✅ | 5 min | On admin update |
| Field definitions | ✅ | 5 min | On admin update |
| Feature data | ❌ | - | Too volatile |
| Query results | ❌ | - | Too many variations |
| MVT tiles | ✅ (HTTP) | 1 hr | `Cache-Control` header |

---

### Cross-Cutting Summary

| Concern | MVP Implementation | Portable From Existing? |
|---------|-------------------|------------------------|
| **Auth** | Admin password (X-API-Key header) | Yes - simplify `ApiKeyAuthenticationHandler` |
| **Health** | Database + self checks | Yes - extract from `HealthCheckExtensions` |
| **Validation** | Manual + endpoint filters (AOT-safe) | No - fresh implementation |
| **SQL Injection** | Parameterized queries + pattern check | Yes - copy `InputValidationHelpers` |
| **CORS** | Static origins list | No - start fresh (simpler) |
| **Request Limits** | Kestrel config | Yes - copy `ConfigureRequestLimits` |
| **Compression** | Brotli + Gzip | No - standard ASP.NET Core |
| **Metadata Cache** | IDistributedCache (Redis optional) | No - cloud-native from day one |

---

## Configuration

### Environment Variables (Primary)

Docker deployments use environment variables exclusively. This is the primary configuration method.

#### Quick Reference

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `ConnectionStrings__DefaultConnection` | ✅ | — | PostGIS connection string |
| `HONUA_ADMIN_PASSWORD` | ❌ | empty | Admin password (empty = no auth in dev) |
| `Cors__AllowedOrigins__0` | ❌ | * in dev | First allowed origin |
| `Cors__AllowedOrigins__1` | ❌ | — | Second allowed origin (and so on) |
| `Basemap__Provider` | ❌ | openfreemap | `openfreemap` or `maptiler` |
| `Basemap__ApiKey` | ❌ | — | MapTiler key (if using maptiler) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | ❌ | — | OpenTelemetry collector endpoint |
| `ASPNETCORE_ENVIRONMENT` | ❌ | Production | `Development` or `Production` |

#### Minimal Production Config

```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=db;Database=honua;Username=honua;Password=secret" \
  -e HONUA_ADMIN_PASSWORD="your-secret-key" \
  ghcr.io/honuaio/honua-server:latest
```

#### With CORS and Observability

```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=db;Database=honua;Username=honua;Password=secret" \
  -e HONUA_ADMIN_PASSWORD="your-secret-key" \
  -e Cors__AllowedOrigins__0="https://myapp.example.com" \
  -e Cors__AllowedOrigins__1="https://admin.example.com" \
  -e OTEL_EXPORTER_OTLP_ENDPOINT="http://collector:4317" \
  ghcr.io/honuaio/honua-server:latest
```

#### With MapTiler Basemap

```bash
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=db;Database=honua;Username=honua;Password=secret" \
  -e Basemap__Provider="maptiler" \
  -e Basemap__ApiKey="your-maptiler-key" \
  ghcr.io/honuaio/honua-server:latest
```

### appsettings.json (Development Only)

For local development without Docker, use appsettings.json. Not used in production.

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=honua;Username=postgres;Password=postgres"
  },
  "HONUA_ADMIN_PASSWORD": "",
  "Cors": {
    "AllowedOrigins": []
  }
}
```

### Strongly-Typed Options

Admin password is read from `HONUA_ADMIN_PASSWORD` directly to keep configuration simple.

```csharp
// Configuration/HonuaOptions.cs
public sealed class HonuaOptions
{
    public CorsOptions Cors { get; init; } = new();
    public BasemapOptions Basemap { get; init; } = new();
}

public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; init; } = [];
}

public sealed class BasemapOptions
{
    public string Provider { get; init; } = "openfreemap";
    public string? ApiKey { get; init; }
}
```

### Environment Variable Mapping

ASP.NET Core maps `__` (double underscore) to configuration hierarchy:

```
ConnectionStrings__DefaultConnection → ConnectionStrings:DefaultConnection
HONUA_ADMIN_PASSWORD          → HONUA_ADMIN_PASSWORD
Cors__AllowedOrigins__0       → Cors:AllowedOrigins[0]
Cors__AllowedOrigins__1       → Cors:AllowedOrigins[1]
Basemap__Provider             → Basemap:Provider
Basemap__ApiKey               → Basemap:ApiKey
```

#### Docker Compose

```yaml
# docker/docker-compose.yml
services:
  honua:
    image: honua-server:latest
    ports:
      - "8080:8080"
    environment:
      # Required
      - ConnectionStrings__DefaultConnection=Host=postgres;Database=honua;Username=postgres;Password=${POSTGRES_PASSWORD}

      # Auth (leave empty to disable admin auth in dev)
      - HONUA_ADMIN_PASSWORD=${HONUA_ADMIN_PASSWORD:-}

      # Observability
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889
      - OTEL_SERVICE_NAME=Honua.Server

      # CORS (for Blazor admin)
      - Cors__AllowedOrigins__0=http://localhost:5173
    depends_on:
      - postgres
      - aspire-dashboard

  postgres:
    image: postgis/postgis:16-3.4
    environment:
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
      - POSTGRES_DB=honua
    volumes:
      - postgres_data:/var/lib/postgresql/data

  aspire-dashboard:
    image: mcr.microsoft.com/dotnet/aspire-dashboard:9.0
    ports:
      - "18888:18888"
    environment:
      - DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true

volumes:
  postgres_data:
```

#### .env File (Development)

```bash
# .env (git-ignored, local development only)
POSTGRES_PASSWORD=localdev123
HONUA_ADMIN_PASSWORD=dev-admin-password-not-for-production
```

#### Kubernetes Secrets

```yaml
# k8s/secret.yaml
apiVersion: v1
kind: Secret
metadata:
  name: honua-secrets
type: Opaque
stringData:
  ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;..."
  HONUA_ADMIN_PASSWORD: "production-secret-key"
---
# k8s/deployment.yaml
apiVersion: apps/v1
kind: Deployment
spec:
  template:
    spec:
      containers:
        - name: honua
          envFrom:
            - secretRef:
                name: honua-secrets
          env:
            - name: OTEL_EXPORTER_OTLP_ENDPOINT
              value: "http://otel-collector:4317"
```

#### Startup Validation

```csharp
// Program.cs - Fail fast on missing required config
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");

// Optional: Validate options on startup
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .ValidateOnStart();
```

#### Never in appsettings.json

| Setting | Why Not |
|---------|---------|
| `ConnectionStrings:DefaultConnection` | Contains password |
| `HONUA_ADMIN_PASSWORD` | Secret |
| Production URLs | Environment-specific |

Keep `appsettings.json` for defaults and structure only. All secrets via environment or secret managers.

### What's NOT in MVP Config

| Deferred | Why |
|----------|-----|
| DataSources dictionary | Single connection string |
| Layers/Services blocks | Database-driven metadata |
| RateLimit (app-level) | Externalized to edge |
| Resilience (hedging/bulkhead) | Keep only circuit breaker |
| Features (GeoETL, etc.) | All enterprise features |
| Cloud (AWS/Azure) | Deferred |
| Alerts | Deferred |
| GitOps | Deferred |
| MultiTenancy | Deferred |
| AI | Deferred |

---

## Native AOT

.NET 10 Native AOT for faster startup and lower memory. Minimal APIs are AOT-friendly by design.

### Project Configuration

```xml
<!-- Honua.Server.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
</Project>
```

### JSON Source Generators (Required for AOT)

```csharp
// Infrastructure/Json/HonuaJsonContext.cs
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(EditResponse))]
[JsonSerializable(typeof(LayerDefinition))]
[JsonSerializable(typeof(FeatureCollection))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(List<LayerDefinition>))]
public partial class HonuaJsonContext : JsonSerializerContext { }

// Program.cs
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HonuaJsonContext.Default);
});
```

### AOT Compatibility Matrix

| Component | AOT Ready | Notes |
|-----------|-----------|-------|
| **Minimal APIs** | ✅ | First-class support |
| **Npgsql** | ✅ | Full AOT support in v8+ |
| **System.Text.Json** | ✅ | With source generators |
| **Polly v8** | ✅ | AOT compatible |
| **StackExchange.Redis** | ✅ | AOT compatible |
| **OpenTelemetry** | ✅ | AOT compatible |

No Dapper, no FluentValidation = no AOT workarounds needed.

### Raw Npgsql Behind Interface

```csharp
// Core/Abstractions/IFeatureStore.cs
public interface IFeatureStore
{
    Task<QueryResult> QueryAsync(string layerId, FeatureQuery query, CancellationToken ct);
    Task<FeatureRecord?> GetAsync(string layerId, long featureId, CancellationToken ct);
    Task<long> CreateAsync(string layerId, FeatureRecord feature, CancellationToken ct);
    Task<bool> UpdateAsync(string layerId, FeatureRecord feature, CancellationToken ct);
    Task<bool> DeleteAsync(string layerId, long featureId, CancellationToken ct);
}

// Postgres/PostgresFeatureStore.cs
public sealed class PostgresFeatureStore : IFeatureStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILayerCatalog _catalog;

    public async Task<QueryResult> QueryAsync(
        string layerId, FeatureQuery query, CancellationToken ct)
    {
        var layer = await _catalog.GetLayerAsync(layerId, 0, ct)
            ?? throw new LayerNotFoundException(layerId, 0);

        var sql = BuildQuerySql(layer, query);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);

        AddParameters(cmd, layer, query);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var features = new List<FeatureRecord>();

        while (await reader.ReadAsync(ct))
        {
            features.Add(MapFeature(reader, layer));
        }

        return new QueryResult(features, query.IncludeCount
            ? await CountAsync(layerId, query, ct)
            : null);
    }

    private static FeatureRecord MapFeature(NpgsqlDataReader reader, LayerDefinition layer)
    {
        var attributes = new Dictionary<string, object?>();

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name == layer.GeometryField) continue;

            attributes[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }

        return new FeatureRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal(layer.ObjectIdField)),
            Geometry = reader.GetFieldValue<NpgsqlTypes.NpgsqlPoint>(
                reader.GetOrdinal(layer.GeometryField)),
            Attributes = attributes
        };
    }
}
```

### Manual Validation (AOT-Safe)

```csharp
// Core/Validation/IValidatable.cs
public interface IValidatable
{
    (bool IsValid, IDictionary<string, string[]> Errors) Validate();
}

// Features/Query/QueryRequest.cs
public record QueryRequest : IValidatable
{
    public string? Where { get; init; }
    public string? OutFields { get; init; }
    public int? ResultRecordCount { get; init; }
    public int? ResultOffset { get; init; }
    public string? Geometry { get; init; }
    public string? GeometryType { get; init; }

    public (bool IsValid, IDictionary<string, string[]> Errors) Validate()
    {
        var errors = new Dictionary<string, string[]>();

        if (ResultRecordCount is < 1 or > 10000)
            errors["resultRecordCount"] = ["Must be between 1 and 10000"];

        if (ResultOffset < 0)
            errors["resultOffset"] = ["Must be non-negative"];

        if (OutFields is not null && OutFields != "*" && !IsValidFieldList(OutFields))
            errors["outFields"] = ["Contains invalid field names"];

        return (errors.Count == 0, errors);
    }

    private static bool IsValidFieldList(string fields) =>
        fields.Split(',').All(f =>
            System.Text.RegularExpressions.Regex.IsMatch(f.Trim(), @"^[a-zA-Z_]\w*$"));
}

// Infrastructure/Filters/ValidationFilter.cs
public sealed class ValidationFilter<T> : IEndpointFilter where T : IValidatable
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        if (ctx.Arguments.OfType<T>().FirstOrDefault() is { } request)
        {
            var (isValid, errors) = request.Validate();
            if (!isValid)
                return Results.ValidationProblem(errors);
        }

        return await next(ctx);
    }
}

// Usage in endpoint
app.MapGet("/rest/services/{serviceId}/FeatureServer/{layerIndex}/query", HandleQuery)
    .AddEndpointFilter<ValidationFilter<QueryRequest>>();
```

### Docker Multi-Stage Build (AOT)

```dockerfile
# docker/Dockerfile.aot
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Install native build dependencies
RUN apk add --no-cache clang build-base zlib-dev

# Copy and restore
COPY ["src/Honua.Server/Honua.Server.csproj", "src/Honua.Server/"]
COPY ["src/Honua.Core/Honua.Core.csproj", "src/Honua.Core/"]
RUN dotnet restore "src/Honua.Server/Honua.Server.csproj"

# Copy source and publish AOT
COPY . .
RUN dotnet publish "src/Honua.Server/Honua.Server.csproj" \
    -c Release \
    -o /app/publish \
    --self-contained \
    -p:PublishAot=true

# Runtime image - no .NET runtime needed!
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine AS runtime
WORKDIR /app

# Copy native binary
COPY --from=build /app/publish .

# Non-root user
RUN adduser -D -u 1000 honua
USER honua

EXPOSE 8080
ENTRYPOINT ["./Honua.Server"]
```

### Performance Comparison

| Metric | JIT | Native AOT | Improvement |
|--------|-----|------------|-------------|
| **Cold start** | ~800ms | ~50ms | 16x faster |
| **Memory (idle)** | ~80MB | ~25MB | 3x smaller |
| **Image size** | ~200MB | ~50MB | 4x smaller |
| **Throughput** | Baseline | ~Same | - |

### Build Modes

```bash
# Standard JIT build (development)
dotnet publish -c Release -o ./publish

# Native AOT build (production)
dotnet publish -c Release -o ./publish -p:PublishAot=true

# Docker builds
docker build -f docker/Dockerfile -t honua:jit .        # JIT
docker build -f docker/Dockerfile.aot -t honua:aot .   # AOT
```

### When to Use AOT

| Scenario | Recommendation |
|----------|----------------|
| **Local development** | JIT (faster build) |
| **CI/CD testing** | JIT (faster pipeline) |
| **Production (containers)** | AOT (faster startup, lower memory) |
| **Serverless/Functions** | AOT (critical for cold starts) |
| **Edge/IoT** | AOT (smaller footprint) |

### AOT Checklist

- [ ] Add `PublishAot=true` to project
- [ ] Create JSON source generator context (`HonuaJsonContext`)
- [ ] Use raw Npgsql (no Dapper) for data access
- [ ] Use `IValidatable` pattern (no FluentValidation)
- [ ] Test all endpoints with AOT build
- [ ] Create AOT-specific Dockerfile
- [ ] Verify no runtime reflection warnings (`dotnet publish` shows them)

---

### Key Patterns from Current Codebase to Keep

1. **`ITransientException` marker** — Clean way to identify retryable errors
2. **RFC 7807 Problem Details** — Standard error format
3. **Resilience pipeline factory** — Configurable, testable resilience
4. **ActivitySource per concern** — Clear trace categorization
5. **Typed loggers** — Semantic log categories
