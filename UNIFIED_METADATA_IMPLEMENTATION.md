# Unified Metadata Service Implementation

This document describes the comprehensive implementation of unified metadata service unification to eliminate separate capabilities/metadata generation per protocol and create shared metadata semantics with protocol-specific formatters.

## Problem Statement

Previously, each protocol generated metadata independently:
- WFS GetCapabilities (XML format)
- ESRI FeatureServer service metadata (JSON format) 
- OGC API Features specifications (OpenAPI/JSON)
- OData service documents (XML/JSON)

This led to:
- **Inconsistencies** across protocols for the same data
- **Code duplication** in metadata generation logic
- **Maintenance overhead** when adding new layers or changing service configuration
- **Performance issues** from redundant metadata computation

## Solution Architecture

The unified metadata system implements a **provider-formatter pattern**:

1. **Unified Metadata Provider** (`IMetadataProvider`) - Single source of truth for metadata collection
2. **Protocol Formatters** (`ICapabilitiesFormatter<T>`) - Protocol-specific formatting from shared metadata
3. **Shared Metadata Models** - Common domain models containing all information needed by protocols

```
┌─────────────────┐    ┌──────────────────────┐    ┌─────────────────────┐
│   WFS Client    │    │   FeatureServer      │    │   OGC API Client    │
└─────────┬───────┘    └──────────┬───────────┘    └─────────┬───────────┘
          │                       │                          │
          ▼                       ▼                          ▼
┌─────────────────┐    ┌──────────────────────┐    ┌─────────────────────┐
│ WFS Formatter   │    │ FeatureServer        │    │ OGC API Formatter   │
│ (XML)           │    │ Formatter (JSON)     │    │ (JSON/OpenAPI)      │
└─────────┬───────┘    └──────────┬───────────┘    └─────────┬───────────┘
          │                       │                          │
          └───────────────────────┼──────────────────────────┘
                                  ▼
                    ┌──────────────────────────┐
                    │   Unified Metadata       │
                    │   Provider               │
                    │   (Single Source)        │
                    └─────────┬────────────────┘
                              │
                              ▼
                    ┌──────────────────────────┐
                    │   Service & Layer        │
                    │   Catalog                │
                    └──────────────────────────┘
```

## Implementation Components

### 1. Core Interfaces

#### `IMetadataProvider`
Central interface for metadata collection from services and layers.

```csharp
public interface IMetadataProvider
{
    Task<ServiceMetadata> GetServiceMetadataAsync(
        HttpContext context, 
        ServiceDefinition service,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default);

    Task<LayerMetadata> GetLayerMetadataAsync(
        HttpContext context, 
        ServiceDefinition service,
        LayerDefinition layer, 
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default);

    Task<GlobalCapabilities> GetGlobalCapabilitiesAsync(
        HttpContext context,
        MetadataProviderOptions options,
        CancellationToken cancellationToken = default);
}
```

#### `ICapabilitiesFormatter<T>`
Protocol-specific formatter interface for converting unified metadata.

```csharp
public interface ICapabilitiesFormatter<TCapabilities>
{
    Task<TCapabilities> FormatServiceCapabilitiesAsync(
        ServiceMetadata serviceMetadata,
        GlobalCapabilities globalCapabilities,
        HttpContext context,
        CancellationToken cancellationToken = default);

    string Protocol { get; }
    IReadOnlyList<string> SupportedMediaTypes { get; }
}
```

### 2. Unified Metadata Models

#### `ServiceMetadata`
Comprehensive service metadata containing all information needed by protocol formatters:
- **Service Identity**: Name, title, description, keywords, licensing
- **Layer Information**: All layers with detailed metadata
- **Capabilities**: Supported operations, formats, spatial/temporal capabilities
- **Access Control**: Authentication and authorization information
- **Protocol Links**: Base URLs for different protocol endpoints

#### `LayerMetadata`
Detailed layer metadata including:
- **Field Schema**: Enhanced field information with statistics and domains
- **Spatial Information**: Geometry types, extents, coordinate systems
- **Temporal Information**: Time fields and temporal extents
- **Style Information**: Default renderers and drawing information
- **Capabilities**: Layer-specific query and edit capabilities

#### `GlobalCapabilities`
Server-wide capabilities including:
- **Server Identity**: Version, contact, provider information
- **Protocol Capabilities**: Supported protocols and their specific capabilities
- **Global Limits**: System-wide constraints and limitations
- **Security**: Authentication methods and access policies

### 3. Protocol Formatters

#### WFS 2.0 Formatter (`Wfs20CapabilitiesFormatter`)
Formats unified metadata into WFS GetCapabilities XML:
- Maps service metadata to WFS ServiceIdentification
- Converts layer metadata to WFS FeatureTypeList
- Transforms capabilities to WFS OperationsMetadata
- Builds filter capabilities from query metadata

#### FeatureServer Formatter (`FeatureServerCapabilitiesFormatter`)
Formats unified metadata into ESRI-compatible JSON:
- Maps service to FeatureServerResponse structure
- Converts layers to LayerInfo with ESRI field types
- Transforms capabilities to ESRI-specific capability strings
- Handles drawing information and advanced query capabilities

#### OGC API Features Formatter (`OgcFeaturesCapabilitiesFormatter`)
Formats unified metadata into OpenAPI 3.0 specifications:
- Generates landing pages with service links
- Creates conformance declarations
- Builds collection metadata
- Produces OpenAPI path definitions

#### OData Formatter (`ODataCapabilitiesFormatter`)
Formats unified metadata into OData service documents:
- Maps layers to OData entity sets
- Converts field types to EDM types
- Generates EDMX metadata documents
- Creates OData service documents

### 4. Performance Optimizations

#### Intelligent Caching
- **Service-level caching** with configurable TTL
- **Layer-level caching** for expensive metadata computations
- **Cache invalidation** based on service/layer changes
- **Memory-efficient** cache with size limits

#### Lazy Computation
- **Expensive metadata** computed only when requested
- **Timeout controls** for expensive operations
- **Graceful degradation** when computations fail
- **Configurable options** to control metadata depth

#### Parallel Processing
- **Concurrent layer processing** for multi-layer services
- **Async/await throughout** for non-blocking operations
- **Cancellation token support** for request timeouts

## Usage Examples

### Basic Service Registration

```csharp
// In Program.cs
builder.ConfigureUnifiedMetadata();

// After app creation
app.UseUnifiedMetadata();
```

### Getting Unified Metadata

```csharp
// Inject the metadata provider
[ApiController]
public class MetadataController(IMetadataProvider metadataProvider) : ControllerBase
{
    [HttpGet("services/{serviceId}/metadata")]
    public async Task<ServiceMetadata> GetServiceMetadata(string serviceId)
    {
        var service = await GetServiceDefinition(serviceId);
        var options = MetadataProviderOptions.Fast(GetBaseUrl());
        
        return await metadataProvider.GetServiceMetadataAsync(
            HttpContext, service, options);
    }
}
```

### Formatting for Specific Protocols

```csharp
// WFS 2.0 capabilities
[HttpGet("services/{serviceId}/wfs/capabilities")]
public async Task<IResult> GetWfsCapabilities(
    string serviceId,
    [FromServices] Wfs20CapabilitiesFormatter formatter)
{
    var metadata = await GetServiceMetadata(serviceId);
    var globalCapabilities = await GetGlobalCapabilities();
    
    var wfsCapabilities = await formatter.FormatServiceCapabilitiesAsync(
        metadata, globalCapabilities, HttpContext);
        
    return Results.Content(SerializeXml(wfsCapabilities), "application/xml");
}

// FeatureServer metadata
[HttpGet("services/{serviceId}/featureserver")]
public async Task<FeatureServerResponse> GetFeatureServerMetadata(
    string serviceId,
    [FromServices] FeatureServerCapabilitiesFormatter formatter)
{
    var metadata = await GetServiceMetadata(serviceId);
    var globalCapabilities = await GetGlobalCapabilities();
    
    return await formatter.FormatServiceCapabilitiesAsync(
        metadata, globalCapabilities, HttpContext);
}
```

## Migration Strategy

### Phase 1: Parallel Implementation
1. ✅ Implement unified metadata interfaces and models
2. ✅ Create protocol-specific formatters
3. ✅ Add unified metadata provider implementation
4. ✅ Create demonstration endpoints

### Phase 2: Integration (Recommended Next Steps)
1. **Update existing WFS endpoints** to use unified formatter
2. **Migrate FeatureServer endpoints** to unified metadata
3. **Replace OGC API Features** metadata generation
4. **Update OData** service document generation

### Phase 3: Cleanup
1. Remove old metadata generation code
2. Remove duplicate capability detection logic
3. Consolidate service description patterns
4. Optimize caching for production workloads

## Benefits Achieved

### ✅ **Consistency**
- Single source of truth for all metadata
- Identical information across all protocols
- Consistent capability reporting

### ✅ **Maintainability** 
- Protocol formatters are isolated and testable
- Adding new protocols requires only a formatter implementation
- Changes to service metadata automatically propagate to all protocols

### ✅ **Performance**
- Shared computation with intelligent caching
- Parallel metadata generation for multi-layer services
- Configurable expensive metadata computation

### ✅ **Extensibility**
- Easy to add new protocols with `ICapabilitiesFormatter<T>`
- Pluggable metadata enrichment through provider options
- Support for custom protocol extensions

## Testing Strategy

The unified metadata system includes comprehensive testing endpoints at `/unified-metadata/*`:

- **`/unified-metadata/capabilities`** - Raw global capabilities
- **`/unified-metadata/services/{serviceId}`** - Raw service metadata  
- **`/unified-metadata/services/{serviceId}/layers/{layerId}`** - Raw layer metadata
- **`/unified-metadata/services/{serviceId}/wfs-capabilities`** - WFS formatted output
- **`/unified-metadata/services/{serviceId}/featureserver-capabilities`** - ESRI formatted output
- **`/unified-metadata/services/{serviceId}/ogc-features-capabilities`** - OGC API formatted output
- **`/unified-metadata/services/{serviceId}/odata-capabilities`** - OData formatted output

These endpoints demonstrate the same metadata formatted for different protocols, validating consistency and completeness.

## Monitoring and Observability

The implementation includes comprehensive logging through structured logging extensions:
- **Performance metrics** for metadata generation and formatting
- **Cache hit/miss ratios** for optimization
- **Error tracking** for failed metadata operations
- **Request tracing** through OpenTelemetry integration

## Future Enhancements

1. **Protocol auto-discovery** - Automatically detect supported protocols per service
2. **Custom metadata extensions** - Allow plugins to extend metadata models
3. **Real-time updates** - Cache invalidation through service change events
4. **Metadata versioning** - Support for metadata evolution and backwards compatibility

This unified metadata implementation eliminates protocol-specific metadata silos and provides a foundation for consistent, maintainable geospatial service metadata across all supported protocols.