# Honua Server Codebase Structure

## Project Organization Overview

The Honua Server follows a **vertical slice architecture** organized by feature rather than technical layers. This greenfield codebase is currently in Phase 0 with minimal implementation.

## Solution Structure
```
honua-server/
├── src/                           # Source code
│   ├── Honua.Server/              # Main host (Minimal APIs) - Entry point
│   ├── Honua.Core/                # Domain models, abstractions
│   ├── Honua.Postgres/            # PostgreSQL implementation  
│   └── Honua.Admin/               # Blazor WASM admin UI (planned Phase 4)
├── tests/                         # Test projects
│   ├── Honua.TestKit/             # Shared test infrastructure
│   ├── Honua.Core.Tests/          # Unit tests (no I/O)
│   ├── Honua.Server.Tests/        # Integration tests
│   └── Honua.Architecture.Tests/  # Architecture enforcement (planned)
├── docs/                          # Documentation
│   ├── adr/                       # Architecture Decision Records
│   ├── ARCHITECTURE.md            # Detailed architecture plan
│   ├── MVP_PLAN.md                # Complete MVP specification
│   └── ROADMAP.md                 # Post-MVP roadmap
├── .github/workflows/             # CI/CD pipeline
├── scripts/                       # Utility scripts
└── Honua.sln                      # Solution file
```

## Source Projects (`src/`)

### Honua.Server (Main Host)
**Purpose**: ASP.NET Core Minimal APIs host, composition root
**Current State**: Phase 0 - basic Program.cs with health endpoints only
**Planned Structure**:
```
Honua.Server/
├── Program.cs                     # Composition root, DI setup
├── Endpoints/                     # API endpoint modules (vertical slices)
│   ├── FeatureServer/             # GeoServices REST endpoints
│   │   ├── QueryEndpoint.cs       # Query handler
│   │   ├── ApplyEditsEndpoint.cs  # Edit operations
│   │   └── MetadataEndpoint.cs    # Layer metadata
│   ├── OgcFeatures/               # OGC API Features endpoints
│   │   ├── CollectionsEndpoint.cs # Collections API
│   │   ├── ItemsEndpoint.cs       # Items/features API
│   │   └── TransactionsEndpoint.cs# CREATE/UPDATE/DELETE
│   ├── OData/                     # OData v4 endpoints
│   │   └── ODataEndpoint.cs       # Query/CRUD for Excel/Power BI
│   └── Admin/                     # Admin API endpoints
│       ├── LayersEndpoint.cs      # Layer management
│       └── ConnectionsEndpoint.cs # Data source connections
├── Services/                      # Application services
├── Infrastructure/                # Cross-cutting concerns
│   ├── Logging/                   # Structured logging setup
│   ├── Authentication/            # OIDC middleware
│   ├── Caching/                   # Redis/in-memory cache
│   └── Middleware/                # Custom middleware
└── Configuration/                 # Strongly-typed config
```

**Key Patterns**:
- **Minimal APIs**: No controllers, direct endpoint mapping
- **Vertical Slices**: Each protocol (FeatureServer, OGC, OData) is self-contained
- **Explicit DI**: No assembly scanning (AOT compatibility)
- **Source Generators**: JSON serialization, logging (AOT safe)

### Honua.Core (Domain)
**Purpose**: Domain models, abstractions, shared contracts
**Current State**: Phase 0 - empty project structure
**Planned Structure**:
```
Honua.Core/
├── Models/                        # Domain entities
│   ├── Feature.cs                 # GeoJSON feature
│   ├── Layer.cs                   # Layer metadata
│   ├── Geometry.cs                # Spatial geometry
│   └── Query.cs                   # Query specification
├── Abstractions/                  # Interfaces
│   ├── IFeatureStore.cs           # Data access abstraction
│   ├── IQueryBuilder.cs           # Query building
│   └── IGeometryService.cs        # Spatial operations
├── Queries/                       # Query builders, filters
│   ├── FilterAst/                 # Shared filter AST
│   ├── CqlParser/                 # OGC CQL2 parsing
│   ├── ODataParser/               # OData $filter parsing
│   └── EsriWhereParser/           # Esri WHERE parsing
└── Extensions/                    # Extension methods, utilities
```

**Key Patterns**:
- **Shared Filter AST**: Protocol-agnostic filter representation
- **Immutable Models**: Records, readonly properties
- **Interface Segregation**: Small, focused contracts

### Honua.Postgres (Data Layer)
**Purpose**: PostgreSQL + PostGIS implementation
**Current State**: Phase 0 - empty project structure  
**Planned Structure**:
```
Honua.Postgres/
├── Repositories/                  # Data access implementations
│   ├── PostgresFeatureStore.cs   # Feature CRUD operations
│   ├── PostgresLayerStore.cs     # Layer metadata
│   └── PostgresQueryBuilder.cs   # SQL generation
├── Migrations/                    # Database schema (DbUp)
│   ├── 001_InitialSchema.sql     # Core tables
│   ├── 002_SpatialIndexes.sql    # PostGIS optimization
│   └── Migration.cs              # DbUp runner
├── Mappers/                       # SQL result mapping
│   ├── FeatureMapper.cs          # Row -> Feature
│   └── GeometryMapper.cs         # WKB -> Geometry
└── Extensions/                    # PostGIS helpers
    ├── SpatialExtensions.cs      # ST_* function wrappers
    └── NpgsqlExtensions.cs       # ADO.NET helpers
```

**Key Patterns**:
- **Raw ADO.NET**: Direct Npgsql usage (no ORM/EF)
- **Connection Pooling**: NpgsqlDataSource with pooling
- **Prepared Statements**: Automatic statement caching
- **Streaming**: Large result sets via `IAsyncEnumerable`

### Honua.Admin (Planned - Phase 4)
**Purpose**: Blazor WebAssembly admin interface
**Current State**: Not yet created
**Planned Structure**:
```
Honua.Admin/
├── Pages/                         # Razor components
│   ├── Connections.razor          # Data source management
│   ├── Layers.razor               # Layer publishing
│   ├── Import.razor               # File import wizard
│   └── Styles.razor               # MapLibre style editor
├── Services/                      # API clients  
│   ├── LayerService.cs            # Layer management API
│   └── ImportService.cs           # File import API
├── Shared/                        # Shared components
│   ├── MapPreview.razor           # MapLibre map control
│   └── FileUpload.razor           # File upload component
└── wwwroot/                       # Static assets
    ├── index.html                 # SPA shell
    └── maputnik/                  # Embedded Maputnik editor
```

## Test Projects (`tests/`)

### Honua.TestKit (Test Infrastructure)
**Purpose**: Shared test utilities, fixtures, builders
**Structure**:
```
Honua.TestKit/
├── Fixtures/                      # Test fixtures
│   ├── PostgresFixture.cs        # Testcontainers PostgreSQL
│   └── WebAppFixture.cs          # TestServer for integration tests
├── Builders/                      # Test data builders
│   ├── FeatureBuilder.cs         # Feature test data
│   └── QueryBuilder.cs           # Query test data
├── Attributes/                    # Custom test attributes
│   ├── IntegrationTestAttribute.cs
│   ├── ProtocolAttribute.cs       # Mark protocol tests
│   └── OperationAttribute.cs      # Mark operation tests
└── Extensions/                    # Test assertion helpers
    └── HttpAssertions.cs          # HTTP response helpers
```

### Honua.Core.Tests (Unit Tests)
**Purpose**: Pure unit tests, no I/O dependencies
**Structure**:
```
Honua.Core.Tests/
├── Query/                         # Query parsing tests
│   ├── CqlFilterParserTests.cs
│   ├── ODataFilterParserTests.cs
│   └── EsriWhereParserTests.cs
├── Geometry/                      # Geometry operation tests
│   └── EnvelopeTests.cs
└── Models/                        # Domain model tests
    └── FeatureTests.cs
```

### Honua.Server.Tests (Integration Tests)
**Purpose**: Full HTTP endpoint tests with real database
**Structure**:
```
Honua.Server.Tests/
├── FeatureServer/                 # GeoServices REST tests
│   ├── QueryEndpointTests.cs
│   ├── ApplyEditsEndpointTests.cs
│   └── MetadataEndpointTests.cs
├── OgcFeatures/                   # OGC API Features tests
│   ├── CollectionsEndpointTests.cs
│   ├── ItemsEndpointTests.cs
│   └── ConformanceTests.cs        # OGC CITE compliance
├── OData/                         # OData v4 tests
│   └── ODataEndpointTests.cs
└── Admin/                         # Admin API tests
    └── LayerManagementTests.cs
```

## Configuration & Build System

### Directory.Build.props
Global MSBuild properties:
- **.NET 10**: Latest LTS with preview C# features
- **Nullable Enabled**: Strict null checking
- **Warnings as Errors**: Zero tolerance for warnings
- **AOT Ready**: Native AOT compilation supported

### .editorconfig
Comprehensive code style enforcement:
- **C# Conventions**: File-scoped namespaces, expression-bodied members
- **Naming Rules**: PascalCase, camelCase, underscore fields
- **Formatting**: Allman braces, 4-space indent, LF line endings
- **License Headers**: Required on all C# files

### CI/CD (.github/workflows/)
```
.github/workflows/
├── ci.yml                         # Build, test, format, security
├── benchmarks.yml                 # Performance regression detection
└── release.yml                    # Container image publishing
```

## Documentation Structure (`docs/`)

### Architecture Decision Records (`docs/adr/`)
Currently 11 ADRs covering:
- Data access patterns (raw Npgsql vs ORM)
- Protocol implementations (OData full CRUD)
- Infrastructure decisions (proxy rate limiting, DbUp migrations)
- UI architecture (Blazor WASM, embedded Maputnik)

### Key Documentation Files
- **ARCHITECTURE.md**: 2,000+ line detailed architecture specification
- **MVP_PLAN.md**: 2,400+ line implementation plan with phases
- **ROADMAP.md**: Post-MVP feature roadmap (Beta, GA, Later phases)

## Development Principles

### Vertical Slice Architecture
- **Feature Organization**: Code grouped by business capability
- **Protocol Isolation**: Each protocol (FeatureServer, OGC, OData) is independent  
- **Minimal Cross-Cutting**: Shared code only when truly needed

### Quality-First Approach
- **Tests First**: Integration tests with real database required
- **Performance Aware**: Benchmarks, AOT compilation, zero-allocation patterns
- **Security Built-In**: Input validation, parameterized queries, secure defaults

### Phase-Based Growth
- **Phase 0**: Foundation (current) - build/test infrastructure
- **Phase 1-3**: Core protocols implementation
- **Phase 4-5**: Admin UI and production hardening
- **Strict Boundaries**: No features from future phases

This structure supports the greenfield goal of building a focused, high-quality geospatial server while maintaining clear architectural boundaries and comprehensive testing.