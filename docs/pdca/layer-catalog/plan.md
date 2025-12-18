# Plan: ILayerCatalog Abstraction

## Hypothesis
Implement core domain abstractions for layer and service metadata management following clean architecture principles. This will be the foundation for GeoServices REST API endpoints and feature management.

## Expected Outcomes
- **Interface Coverage**: Core abstractions for layer/service metadata
- **Test Coverage**: Integration tests for PostgreSQL implementation
- **Architecture**: Clean separation of domain models from infrastructure
- **GeoServices Foundation**: Ready for REST API endpoint implementation

## Design Decisions

### Domain Models
```csharp
LayerDefinition:
- Id: int (layer identifier)
- Name: string (display name)
- Description: string (optional description)
- GeometryType: GeometryType (Point, LineString, Polygon, etc.)
- SpatialReference: SpatialReference (coordinate system info)
- Fields: FieldDefinition[] (attribute schema)
- Extent: FeatureExtent? (spatial bounds)

ServiceDefinition:
- Name: string (service name)
- Description: string (service description)
- Layers: LayerDefinition[] (available layers)
- SpatialReference: SpatialReference (default coordinate system)
- MaxRecordCount: int (query limit)

FieldDefinition:
- Name: string (field name)
- Type: FieldType (String, Integer, Double, DateTime, Geometry)
- Length: int? (for string fields)
- Nullable: bool (allows null values)
```

### Interface Design
```csharp
ILayerCatalog:
- GetLayerAsync(int layerId) → LayerDefinition?
- ListLayersAsync() → LayerDefinition[]
- GetServiceAsync(string serviceName) → ServiceDefinition?
```

## Implementation Strategy
1. **Core Domain Models**: Define in Honua.Core/Domain/Catalog/
2. **Core Interface**: ILayerCatalog in Honua.Core/Abstractions/
3. **Infrastructure**: PostgresLayerCatalog in Honua.Postgres/Catalog/
4. **DI Registration**: Add to PostgreSQL service collection extensions
5. **Testing**: Integration tests with Testcontainers

## Risks & Mitigation
- **Risk**: Over-designing domain models before understanding all use cases
  **Mitigation**: Start with minimal GeoServices-compatible model, iterate
- **Risk**: PostgreSQL schema assumptions without proper design
  **Mitigation**: Reference legacy implementation patterns, use standard PostGIS conventions
- **Risk**: Complex spatial reference system handling
  **Mitigation**: Start with simple SRID integers, enhance later

## Success Criteria
- [ ] Domain models compile and follow clean architecture
- [ ] PostgreSQL implementation can retrieve layer metadata
- [ ] Integration tests pass with real PostgreSQL/PostGIS
- [ ] DI container properly resolves ILayerCatalog
- [ ] Code follows project conventions (warnings as errors, AOT compatible)