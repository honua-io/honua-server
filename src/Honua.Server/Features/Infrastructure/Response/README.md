# Unified Response Formatting System

This document describes the unified response formatting system that consolidates response generation across all protocols (GeoServices, OGC API Features, WFS 2.0, OData) into a single, consistent architecture.

## Overview

The unified response system solves the problem of scattered format conversion logic by:

1. **Centralizing response preparation** in `IResponseBuilder`
2. **Standardizing response data** in the `ResponseData` model
3. **Providing protocol-specific serializers** via `IProtocolSerializer<T>`
4. **Offering a single service interface** through `IUnifiedResponseService`

## Architecture

### Core Components

#### IResponseBuilder
Prepares standardized response data from domain objects:
```csharp
public interface IResponseBuilder
{
    ValueTask<ResponseData> BuildFeatureCollectionAsync(QueryResult<Feature> queryResult, LayerDefinition layer, ResponseBuildOptions options, CancellationToken cancellationToken = default);
    ValueTask<ResponseData> BuildSingleFeatureAsync(Feature feature, LayerDefinition layer, ResponseBuildOptions options, CancellationToken cancellationToken = default);
    ResponseData BuildErrorResponse(ResponseError error, ResponseBuildOptions? options = null);
    IAsyncEnumerable<StreamingResponseChunk> BuildStreamingResponseAsync(IAsyncEnumerable<Feature> features, LayerDefinition layer, ResponseMetadata metadata, ResponseBuildOptions options, CancellationToken cancellationToken = default);
}
```

#### ResponseData
Unified intermediate representation:
```csharp
public sealed record ResponseData
{
    public ResponseType Type { get; init; }
    public ImmutableArray<ResponseFeature> Features { get; init; }
    public ResponseMetadata Metadata { get; init; }
    public ResponseError? Error { get; init; }
    public LayerDefinition? Layer { get; init; }
    public PaginationInfo? Pagination { get; init; }
    public ImmutableArray<ResponseLink> Links { get; init; }
    public IReadOnlyDictionary<string, object?>? ProtocolMetadata { get; init; }
}
```

#### IProtocolSerializer<TOptions>
Protocol-specific serialization:
```csharp
public interface IProtocolSerializer<in TOptions> where TOptions : class
{
    string Protocol { get; }
    ValueTask<SerializedResponse> SerializeAsync(ResponseData responseData, TOptions options, CancellationToken cancellationToken = default);
    ValueTask<string> SerializeToStreamAsync(ResponseData responseData, PipeWriter outputStream, TOptions options, CancellationToken cancellationToken = default);
    ValueTask<string> SerializeStreamingAsync(IAsyncEnumerable<StreamingResponseChunk> streamingResponse, PipeWriter outputStream, TOptions options, CancellationToken cancellationToken = default);
    string GetContentType(ResponseData responseData, TOptions options);
    IReadOnlyDictionary<string, string> GetHeaders(ResponseData responseData, TOptions options);
}
```

#### IUnifiedResponseService
High-level service interface:
```csharp
public interface IUnifiedResponseService
{
    ValueTask<IResult> CreateFeatureCollectionResponseAsync<TOptions>(string protocol, QueryResult<Feature> queryResult, LayerDefinition layer, ResponseBuildOptions buildOptions, TOptions serializationOptions, CancellationToken cancellationToken = default) where TOptions : class;
    ValueTask<IResult> CreateSingleFeatureResponseAsync<TOptions>(string protocol, Feature feature, LayerDefinition layer, ResponseBuildOptions buildOptions, TOptions serializationOptions, CancellationToken cancellationToken = default) where TOptions : class;
    ValueTask<IResult> CreateStreamingFeatureCollectionResponseAsync<TOptions>(string protocol, IAsyncEnumerable<Feature> features, LayerDefinition layer, ResponseMetadata metadata, ResponseBuildOptions buildOptions, TOptions serializationOptions, CancellationToken cancellationToken = default) where TOptions : class;
    ResponseBuildOptions CreateBuildOptions(HttpRequest request, string protocol, bool includeGeometry = true, string[]? outFields = null, int? outputSrid = null);
    TOptions CreateSerializationOptions<TOptions>(HttpRequest request, string? format = null) where TOptions : class, new();
}
```

## Supported Protocols

### GeoServices (ArcGIS REST API)
- **Serializer**: `GeoServicesSerializer`
- **Options**: `GeoServicesSerializationOptions`
- **Formats**: JSON, JSONP
- **Features**: Field metadata, geometry normalization, ESRI spatial reference

### OGC API Features
- **Serializer**: `OgcApiFeaturesSerializer`
- **Options**: `OgcApiFeaturesSerializationOptions`
- **Formats**: GeoJSON, HTML
- **Features**: Hypermedia links, bbox calculation, browser-friendly HTML

### WFS 2.0
- **Serializer**: `Wfs20Serializer`
- **Options**: `Wfs20SerializationOptions`
- **Formats**: GML 3.2 XML
- **Features**: Schema location, namespace handling, WFS exception reports

### OData v4
- **Serializer**: `ODataSerializer`
- **Options**: `ODataSerializationOptions`
- **Formats**: JSON with OData metadata levels
- **Features**: Entity IDs, context URLs, spatial geometry support

## Usage Examples

### Basic Usage
```csharp
public async Task<IResult> QueryFeatures(
    IUnifiedResponseService unifiedService,
    QueryResult<Feature> queryResult,
    LayerDefinition layer,
    HttpRequest request)
{
    // Create build options
    var buildOptions = unifiedService.CreateBuildOptions(
        request, 
        "GeoServices",
        includeGeometry: true);

    // Create serialization options
    var serializationOptions = unifiedService.CreateSerializationOptions<GeoServicesSerializationOptions>(request);

    // Generate response
    return await unifiedService.CreateFeatureCollectionResponseAsync(
        "GeoServices", 
        queryResult, 
        layer, 
        buildOptions, 
        serializationOptions);
}
```

### Streaming Response
```csharp
public async Task<IResult> StreamFeatures(
    IUnifiedResponseService unifiedService,
    IAsyncEnumerable<Feature> features,
    LayerDefinition layer,
    HttpRequest request)
{
    var buildOptions = unifiedService.CreateBuildOptions(request, "OGC API Features");
    var serializationOptions = unifiedService.CreateSerializationOptions<OgcApiFeaturesSerializationOptions>(request);
    
    var metadata = new ResponseMetadata(TotalCount: 1000, HasMoreResults: true);

    return await unifiedService.CreateStreamingFeatureCollectionResponseAsync(
        "OGC API Features",
        features,
        layer,
        metadata,
        buildOptions,
        serializationOptions);
}
```

### Error Handling
```csharp
public async Task<IResult> HandleError(
    IUnifiedResponseService unifiedService,
    string protocol,
    Exception exception,
    HttpRequest request)
{
    var serializationOptions = unifiedService.CreateSerializationOptions<GeoServicesSerializationOptions>(request);

    return await unifiedService.CreateErrorResponseAsync(
        protocol, 
        exception, 
        serializationOptions);
}
```

## Migration Guide

### Step 1: Gradual Migration with Legacy Helpers
Use the migration helpers to convert existing endpoints with minimal changes:

```csharp
// Old way
var (response, contentType) = await queryFormatter.FormatQueryResultAsync(queryResult, layer, format, ...);
return Results.Json(response, contentType: contentType);

// New way (minimal changes)
return await unifiedService.CreateLegacyGeoServicesResponseAsync(queryResult, layer, format, ...);
```

### Step 2: Full Migration
Rewrite endpoints to use the unified system:

```csharp
public async Task<IResult> ModernEndpoint(
    IUnifiedResponseService unifiedService,
    QueryResult<Feature> queryResult,
    LayerDefinition layer,
    HttpRequest request)
{
    var protocol = DetermineProtocol(request.Path);
    var buildOptions = unifiedService.CreateBuildOptions(request, protocol);
    
    // Protocol-specific serialization options
    return protocol switch
    {
        "GeoServices" => await CreateGeoServicesResponse(unifiedService, queryResult, layer, buildOptions, request),
        "OGC API Features" => await CreateOgcResponse(unifiedService, queryResult, layer, buildOptions, request),
        // ... etc
    };
}
```

### Step 3: Remove Legacy Formatters
Once migration is complete, remove the old protocol-specific formatters:
- `IQueryFormatter` and implementations
- `OgcResponseFormatter` static methods  
- `ODataUtilityService` response methods
- Protocol-specific formatting logic

## Configuration

### Service Registration
```csharp
public void ConfigureServices(IServiceCollection services)
{
    services.AddUnifiedResponseServices();
    
    // Optional: custom configuration
    services.ConfigureResponseFormatting(options =>
    {
        options.EnableGeometryFormatCache = true;
        options.GeometryFormatCacheSizeMb = 100;
        options.DefaultStreamingFlushInterval = 32;
    });
}
```

### Protocol-Specific Configuration
```csharp
services.ConfigureResponseFormatting(options =>
{
    options.ProtocolOptions["GeoServices"] = new ProtocolSpecificOptions
    {
        StreamingFlushInterval = 16,
        Properties = { ["PrettyPrintDefault"] = true }
    };
});
```

## Performance Considerations

### Geometry Format Caching
The system caches converted geometries to avoid repeated transformation:
- WKB → GeoJSON
- WKB → GML
- WKB → GeoServices JSON

### Streaming Support
Large result sets are handled efficiently via streaming:
- Incremental JSON/XML writing
- Configurable flush intervals
- Memory-efficient processing

### Connection Pooling
Geometry readers/writers are pooled for performance:
- `WkbReaderCache.Get()` for WKB reading
- Shared GeoJSON/GML writers

## Testing

### Unit Tests
Test individual components in isolation:
```csharp
[Test]
public async Task ResponseBuilder_BuildsFeatureCollection()
{
    var builder = new ResponseBuilder(limitsOptions, logger);
    var result = await builder.BuildFeatureCollectionAsync(queryResult, layer, options);
    
    Assert.That(result.Type, Is.EqualTo(ResponseType.FeatureCollection));
    Assert.That(result.Features, Has.Length.EqualTo(expected));
}
```

### Integration Tests
Test end-to-end response generation:
```csharp
[Test]
public async Task UnifiedService_CreatesGeoServicesResponse()
{
    var result = await unifiedService.CreateFeatureCollectionResponseAsync(
        "GeoServices", queryResult, layer, buildOptions, serializationOptions);
    
    Assert.That(result, Is.TypeOf<JsonHttpResult>());
}
```

### Protocol Compliance Tests
Verify each protocol generates compliant output:
```csharp
[Test]
public async Task GeoServicesSerializer_GeneratesValidEsriJson()
{
    var serialized = await serializer.SerializeAsync(responseData, options);
    var json = JsonDocument.Parse((string)serialized.Data);
    
    Assert.That(json.RootElement.GetProperty("features").GetArrayLength(), Is.GreaterThan(0));
}
```

## Benefits

### For Developers
- **Single API**: One service interface for all protocols
- **Type Safety**: Strong typing with protocol-specific options
- **Consistency**: Uniform behavior across all endpoints
- **Testability**: Easy to mock and unit test

### For Operations
- **Performance**: Shared geometry processing and caching
- **Maintainability**: Single codebase for response formatting
- **Observability**: Centralized metrics and logging
- **Reliability**: Consistent error handling

### For Standards Compliance
- **Protocol Accuracy**: Each serializer ensures spec compliance
- **Format Validation**: Automated testing of output formats
- **Version Support**: Easy to add new protocol versions
- **Interoperability**: Consistent behavior across clients

## Future Enhancements

### Planned Features
- **Additional Protocols**: KML, Shapefile, MapML
- **Advanced Caching**: Redis-based geometry format cache
- **Compression**: Built-in gzip/brotli support
- **Metrics**: Detailed performance instrumentation

### Extension Points
- **Custom Serializers**: Implement `IProtocolSerializer<T>`
- **Format Converters**: Add new geometry format support
- **Response Processors**: Custom post-processing pipelines
- **Protocol Detection**: Automatic protocol detection from requests