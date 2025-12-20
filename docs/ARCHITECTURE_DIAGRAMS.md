# Honua Architecture Diagrams

Visual representations of the Honua system architecture using Mermaid diagrams.

> **Note:** These diagrams follow the [C4 Model](https://c4model.com/) conventions where applicable.

---

## 1. System Context Diagram

Shows Honua in the context of its users and external systems.

```mermaid
C4Context
    title System Context Diagram - Honua Feature Server

    Person(gisUser, "GIS User", "Uses desktop/web GIS clients")
    Person(analyst, "Data Analyst", "Uses BI tools for reporting")
    Person(developer, "Developer", "Integrates with applications")
    Person(admin, "Administrator", "Manages layers and services")

    System(honua, "Honua Feature Server", "Multi-protocol geospatial feature server")

    System_Ext(geoservices_client, "GeoServices REST Client", "GIS platform supporting GeoServices REST")
    System_Ext(qgis, "QGIS", "Open source GIS")
    System_Ext(maplibre, "MapLibre GL", "Web mapping library")
    System_Ext(excel, "Excel/Power BI", "Microsoft BI tools")
    System_Ext(postgis, "PostgreSQL + PostGIS", "Geospatial database")

    Rel(gisUser, geoservices_client, "Uses")
    Rel(gisUser, qgis, "Uses")
    Rel(developer, maplibre, "Builds with")
    Rel(analyst, excel, "Analyzes in")
    Rel(admin, honua, "Manages via Admin UI")

    Rel(geoservices_client, honua, "FeatureServer REST", "HTTPS")
    Rel(qgis, honua, "OGC API Features", "HTTPS")
    Rel(maplibre, honua, "Vector Tiles (MVT)", "HTTPS")
    Rel(excel, honua, "OData v4", "HTTPS")

    Rel(honua, postgis, "Reads/Writes", "TCP/5432")
```

### Simplified Version (GitHub-compatible)

```mermaid
graph TB
    subgraph Clients
        GeoServicesClient[GeoServices REST Client]
        QGIS[QGIS]
        MapLibre[MapLibre GL]
        Excel[Excel/Power BI]
        Admin[Admin User]
    end

    subgraph "Honua Feature Server"
        API[Honua API]
        AdminUI[Admin UI]
    end

    subgraph Infrastructure
        PostGIS[(PostgreSQL + PostGIS)]
    end

    GeoServicesClient -->|FeatureServer REST| API
    QGIS -->|OGC API Features| API
    MapLibre -->|MVT Tiles| API
    Excel -->|OData v4| API
    Admin -->|HTTPS| AdminUI

    API --> PostGIS
    AdminUI --> API
```

---

## 2. Container Diagram

Shows the deployable units that make up Honua.

```mermaid
graph TB
    subgraph "Honua System"
        subgraph "Application Containers"
            Server["<b>Honua.Server</b><br/><i>ASP.NET Core API</i><br/><br/>Hosts all protocol endpoints:<br/>• FeatureServer REST<br/>• OGC API Features<br/>• OData v4<br/>• Vector Tiles<br/>• Admin API"]

            AdminUI["<b>Honua.Admin</b><br/><i>Blazor WebAssembly</i><br/><br/>Admin interface:<br/>• Connection management<br/>• Layer publishing<br/>• File import<br/>• Style editing"]
        end

        subgraph "Infrastructure"
            DB[("<b>PostgreSQL + PostGIS</b><br/><i>Database</i><br/><br/>• Feature storage<br/>• Spatial operations<br/>• MVT generation")]

            Redis[("<b>Redis</b><br/><i>Cache (Optional)</i><br/><br/>• Metadata cache<br/>• Output cache")]
        end
    end

    AdminUI -->|"API calls<br/>HTTPS"| Server
    Server -->|"SQL/WKB<br/>TCP:5432"| DB
    Server -.->|"Cache ops<br/>TCP:6379"| Redis

    style Server fill:#4A90D9,stroke:#2E6BA6,color:#fff
    style AdminUI fill:#7CB342,stroke:#558B2F,color:#fff
    style DB fill:#FF9800,stroke:#E65100,color:#fff
    style Redis fill:#9E9E9E,stroke:#616161,color:#fff
```

### Container Responsibilities

| Container | Technology | Responsibility |
|-----------|------------|----------------|
| **Honua.Server** | ASP.NET Core 10, Native AOT | API host, business logic, protocol translation |
| **Honua.Admin** | Blazor WebAssembly | Admin UI, served at `/admin` |
| **PostgreSQL + PostGIS** | PostgreSQL 16, PostGIS 3.4 | Data storage, spatial ops, MVT generation |
| **Redis** (optional) | Redis 7 | Metadata caching, output caching |

---

## 3. Component Diagram - Honua.Server

Shows the internal structure of the main API container.

```mermaid
graph TB
    subgraph "Honua.Server Container"
        subgraph "API Layer (Minimal APIs)"
            FS[FeatureServer<br/>Endpoints]
            OGC[OGC Features<br/>Endpoints]
            OData[OData v4<br/>Endpoints]
            MVT[Vector Tile<br/>Endpoints]
            AdminAPI[Admin API<br/>Endpoints]
            Health[Health<br/>Endpoints]
        end

        subgraph "Feature Slices"
            Query[Query Slice<br/><i>QueryHandler, QueryParser</i>]
            Edit[Edit Slice<br/><i>EditHandler, Validator</i>]
            Attach[Attachment Slice<br/><i>AttachmentHandler</i>]
            Tiles[VectorTile Slice<br/><i>TileHandler, TileMath</i>]
            Import[Import Slice<br/><i>ImportService, FileReaders</i>]
            Meta[Metadata Slice<br/><i>MetadataBuilder</i>]
        end

        subgraph "Honua.Core (Library)"
            Store[IFeatureStore]
            Catalog[ILayerCatalog]
            UoW[IUnitOfWork]
            Models[Domain Models<br/><i>FeatureRecord, LayerDefinition</i>]
            FilterAST[Filter AST<br/><i>Shared filter representation</i>]
        end

        subgraph "Honua.Postgres (Library)"
            PgStore[PostgresFeatureStore]
            PgCatalog[PostgresLayerCatalog]
            PgUoW[PostgresUnitOfWork]
            SqlBuilder[SQL Query Builder]
        end

        subgraph "Infrastructure"
            Auth[OIDC Auth]
            Logging[Serilog Logging]
            OTel[OpenTelemetry]
            Resilience[Polly Resilience]
        end
    end

    FS --> Query
    FS --> Edit
    FS --> Attach
    FS --> Meta
    OGC --> Query
    OGC --> Edit
    OData --> Query
    OData --> Edit
    MVT --> Tiles
    AdminAPI --> Import
    AdminAPI --> Meta

    Query --> Store
    Query --> Catalog
    Edit --> Store
    Edit --> UoW
    Tiles --> Store
    Import --> Store

    Store -.-> PgStore
    Catalog -.-> PgCatalog
    UoW -.-> PgUoW

    PgStore --> SqlBuilder
    PgCatalog --> SqlBuilder

    style FS fill:#4A90D9,color:#fff
    style OGC fill:#4A90D9,color:#fff
    style OData fill:#4A90D9,color:#fff
    style MVT fill:#4A90D9,color:#fff
    style FilterAST fill:#E91E63,color:#fff
```

---

## 4. Vertical Slice Structure

Shows how a single feature slice is organized.

```mermaid
graph LR
    subgraph "Query Vertical Slice"
        direction TB
        EP[QueryEndpoint.cs<br/><i>HTTP routing, auth</i>]
        REQ[QueryRequest.cs<br/><i>Strongly-typed input</i>]
        RES[QueryResponse.cs<br/><i>Strongly-typed output</i>]
        PAR[QueryParser.cs<br/><i>HTTP → Domain</i>]
        HAN[QueryHandler.cs<br/><i>Business logic</i>]
        VAL[QueryValidator.cs<br/><i>Input validation</i>]
    end

    HTTP[HTTP Request] --> EP
    EP --> REQ
    REQ --> PAR
    PAR --> VAL
    VAL --> HAN
    HAN --> RES
    RES --> EP
    EP --> RESP[HTTP Response]

    HAN --> STORE[(IFeatureStore)]
    HAN --> CAT[(ILayerCatalog)]
```

### Slice Dependencies (Max Limits)

| Component | Max Dependencies | Typical Dependencies |
|-----------|------------------|----------------------|
| Endpoint | 3-5 | Handler, Validator, Logger |
| Handler | 2-4 | IFeatureStore, ILayerCatalog |
| Parser | 1-2 | Options only |
| Validator | 1-2 | Options, Catalog |

---

## 5. Data Flow Diagram - Query Request

Shows how a query request flows through the system.

```mermaid
sequenceDiagram
    autonumber
    participant Client as GeoServices/OGC Client
    participant EP as QueryEndpoint
    participant Parser as QueryParser
    participant Handler as QueryHandler
    participant Catalog as ILayerCatalog
    participant Store as IFeatureStore
    participant PG as PostgreSQL

    Client->>EP: GET /FeatureServer/0/query?where=...
    EP->>Parser: Parse HTTP request
    Parser-->>EP: QueryRequest

    EP->>Handler: HandleAsync(request)
    Handler->>Catalog: GetLayerAsync(serviceId, layerIndex)
    Catalog->>PG: SELECT FROM honua.layers
    PG-->>Catalog: LayerDefinition
    Catalog-->>Handler: LayerDefinition

    Handler->>Handler: Build FeatureQuery from request
    Handler->>Store: QueryAsync(layerId, query)
    Store->>PG: SELECT ... FROM features WHERE ...
    PG-->>Store: Result rows
    Store-->>Handler: QueryResult

    Handler->>Handler: Build QueryResponse
    Handler-->>EP: QueryResponse
    EP-->>Client: JSON/GeoJSON Response
```

---

## 6. Data Flow Diagram - ApplyEdits Transaction

Shows transaction handling with savepoints.

```mermaid
sequenceDiagram
    autonumber
    participant Client
    participant EP as EditEndpoint
    participant Handler as EditHandler
    participant UoW as IUnitOfWork
    participant TX as ITransactionScope
    participant Store as IFeatureStore
    participant PG as PostgreSQL

    Client->>EP: POST /applyEdits {adds, updates, deletes}
    EP->>Handler: HandleAsync(request)

    Handler->>UoW: BeginTransactionAsync()
    UoW->>PG: BEGIN TRANSACTION
    UoW-->>Handler: ITransactionScope

    loop For each Add
        Handler->>TX: CreateSavepointAsync("add_N")
        TX->>PG: SAVEPOINT add_N
        Handler->>Store: CreateAsync(feature)
        alt Success
            Store->>PG: INSERT INTO features
            PG-->>Store: OK
            Handler->>TX: ReleaseSavepointAsync()
        else Failure
            Handler->>TX: RollbackSavepointAsync()
            TX->>PG: ROLLBACK TO add_N
        end
    end

    alt All succeeded OR rollbackOnFailure=false
        Handler->>TX: CommitAsync()
        TX->>PG: COMMIT
    else Any failed AND rollbackOnFailure=true
        Handler->>TX: RollbackAsync()
        TX->>PG: ROLLBACK
    end

    Handler-->>EP: ApplyEditsResponse
    EP-->>Client: JSON with results per operation
```

---

## 7. Filter Translation Pipeline

Shows how filters from different protocols converge on a shared AST.

```mermaid
graph LR
    subgraph "Protocol Parsers"
        GEOSERVICES[GeoServices WHERE<br/><i>population > 1000</i>]
        CQL[CQL2-Text<br/><i>population > 1000</i>]
        ODATA[OData $filter<br/><i>population gt 1000</i>]
    end

    subgraph "Honua.Core"
        AST[Filter AST<br/><i>BinaryExpression</i><br/><i>PropertyRef, Literal</i>]
    end

    subgraph "SQL Translation"
        SQL[SQL WHERE<br/><i>population > $1</i>]
        SPATIAL[PostGIS Spatial<br/><i>ST_Intersects(...)</i>]
    end

    ESRI --> AST
    CQL --> AST
    ODATA --> AST

    AST --> SQL
    AST --> SPATIAL

    style AST fill:#E91E63,color:#fff
```

### Filter AST Node Types

```
FilterExpression
├── BinaryExpression (AND, OR, =, <>, <, >, etc.)
├── UnaryExpression (NOT, IS NULL)
├── PropertyReference (field name)
├── Literal (string, number, boolean, null)
├── SpatialPredicate (INTERSECTS, CONTAINS, WITHIN)
├── GeometryLiteral (WKT, GeoJSON, Esri JSON)
└── FunctionCall (UPPER, LOWER, etc.)
```

---

## 8. Database Schema (ERD)

```mermaid
erDiagram
    SERVICES ||--o{ LAYERS : contains
    LAYERS ||--o{ LAYER_FIELDS : has
    LAYERS ||--o{ FEATURES : stores

    SERVICES {
        text id PK
        text name
        text description
        boolean enabled
        timestamptz created_at
        timestamptz updated_at
    }

    LAYERS {
        serial id PK
        text service_id FK
        int layer_index
        text name
        text description
        text table_name
        text geometry_field
        text object_id_field
        int srid
        text geometry_type
        float8 extent_xmin
        float8 extent_ymin
        float8 extent_xmax
        float8 extent_ymax
        boolean enabled
        int mvt_min_zoom
        int mvt_max_zoom
        int mvt_max_features
        timestamptz created_at
    }

    LAYER_FIELDS {
        serial id PK
        int layer_id FK
        text name
        text column_name
        text field_type
        text alias
        boolean is_nullable
        boolean is_editable
        int length
    }

    FEATURES {
        bigserial objectid PK
        geometry geom
        jsonb attributes
    }
```

---

## 9. Admin UI Component Structure

```mermaid
graph TB
    subgraph "Honua.Admin (Blazor WASM)"
        subgraph "Pages"
            Dashboard[Dashboard.razor<br/><i>Health overview</i>]
            Connections[Connections.razor<br/><i>DB connection mgmt</i>]
            Layers[Layers.razor<br/><i>Layer list & publish</i>]
            Import[Import.razor<br/><i>File import wizard</i>]
            Preview[Preview.razor<br/><i>Map preview</i>]
            Styles[Styles.razor<br/><i>Maputnik editor</i>]
        end

        subgraph "Components"
            ConnectionForm[ConnectionForm.razor]
            TableSelector[TableSelector.razor]
            LayerConfig[LayerConfigForm.razor]
            FileUploader[FileUploader.razor]
            MapView[MapView.razor<br/><i>MapLibre GL</i>]
        end

        subgraph "Services"
            ApiClient[HonuaApiClient.cs]
            AuthService[AuthService.cs]
            StateContainer[AppState.cs]
        end
    end

    Dashboard --> ApiClient
    Connections --> ConnectionForm
    Connections --> ApiClient
    Layers --> TableSelector
    Layers --> LayerConfig
    Import --> FileUploader
    Preview --> MapView
    Styles --> MapView

    ApiClient -->|HTTPS| API[Honua.Server API]
```

---

## 10. Deployment Architecture

### Kubernetes (Helm)

```mermaid
graph TB
    subgraph "Kubernetes Cluster"
        subgraph "Honua Namespace"
            Ingress[Ingress Controller]
            HPA[HorizontalPodAutoscaler]

            subgraph "Deployment"
                Pod1[Honua Pod 1]
                Pod2[Honua Pod 2]
                PodN[Honua Pod N]
            end

            ConfigMap[ConfigMap<br/><i>Environment config</i>]
            Secret[Secret<br/><i>DB credentials, OIDC</i>]
        end

        subgraph "Data Tier"
            PG[(PostgreSQL<br/>+ PostGIS)]
            Redis[(Redis<br/>Optional)]
        end
    end

    Internet[Internet] --> Ingress
    Ingress --> Pod1
    Ingress --> Pod2
    Ingress --> PodN

    HPA -.-> Pod1
    HPA -.-> Pod2
    HPA -.-> PodN

    Pod1 --> PG
    Pod2 --> PG
    PodN --> PG

    Pod1 -.-> Redis
    Pod2 -.-> Redis
    PodN -.-> Redis

    ConfigMap -.-> Pod1
    Secret -.-> Pod1
```

### Cloud Provider (AWS Example)

```mermaid
graph TB
    subgraph "AWS"
        ALB[Application Load Balancer]

        subgraph "ECS Fargate"
            Task1[Honua Task 1]
            Task2[Honua Task 2]
        end

        subgraph "RDS"
            Aurora[(Aurora PostgreSQL<br/>+ PostGIS)]
        end

        subgraph "ElastiCache"
            RedisCluster[(Redis Cluster)]
        end

        SecretsManager[Secrets Manager]
    end

    Internet[Internet] --> ALB
    ALB --> Task1
    ALB --> Task2

    Task1 --> Aurora
    Task2 --> Aurora
    Task1 -.-> RedisCluster
    Task2 -.-> RedisCluster

    SecretsManager -.-> Task1
    SecretsManager -.-> Task2
```

---

## Quick Reference

| Diagram | Purpose | When to Use |
|---------|---------|-------------|
| System Context | Show external interactions | Stakeholder discussions |
| Container | Show deployable units | Infrastructure planning |
| Component | Show internal structure | Developer onboarding |
| Sequence | Show runtime behavior | Debugging, documentation |
| ERD | Show data model | Database design |

---

## See Also

- [ARCHITECTURE.md](./ARCHITECTURE.md) - Detailed architecture prose
- [MVP_PLAN.md](./MVP_PLAN.md) - Implementation phases and criteria
- [ADRs](./adr/) - Architecture Decision Records
