# ETag Implementation for Honua Server

This document describes the comprehensive ETag support implemented for the Honua Server to improve cache validation and efficiency.

## Overview

The ETag implementation provides HTTP cache validation using strong ETags computed from content hashes. This works in conjunction with the existing ASP.NET Core output caching to provide optimal caching performance.

## Components Implemented

### 1. Core ETag Service (`IETagService` & `ETagService`)

**Location**: `src/Honua.Server/Features/Infrastructure/Caching/`

**Key Features**:
- Efficient SHA256-based content hashing
- Stack allocation for small content (< 256 bytes)
- Strong ETags (quoted) for reliable cache validation
- Support for object serialization with JSON type info
- Conditional request validation (If-None-Match, If-Match)

**Performance Optimizations**:
- Uses `Span<T>` and `stackalloc` to avoid heap allocations for small content
- SHA256 hashing with `TryHashData` for performance
- Base64 encoding without padding for compact representation

### 2. ETag Middleware (`ETagMiddleware`)

**Location**: `src/Honua.Server/Features/Infrastructure/Middleware/ETagMiddleware.cs`

**Functionality**:
- Global ETag support for responses without explicit ETag handling
- Works after output cache middleware to avoid duplicate processing
- Handles conditional requests (304 Not Modified, 412 Precondition Failed)
- Captures response body to compute ETags for cached content

### 3. ETag Endpoint Filter (`ETagEndpointFilter`)

**Location**: `src/Honua.Server/Features/Infrastructure/Caching/ETagEndpointFilter.cs`

**Purpose**:
- More efficient than global middleware for specific endpoints
- Integrates with ASP.NET Core endpoint filters
- Handles JSON results with proper ETag generation
- Custom result wrappers for 304 Not Modified responses

### 4. Extension Methods (`ETagExtensions`)

**Location**: `src/Honua.Server/Features/Infrastructure/Caching/ETagExtensions.cs`

**Provides**:
- Service registration: `services.AddETags()`
- Middleware registration: `app.UseETags()`
- Endpoint decoration: `.WithETag()`

## Integration Points

### 1. Service Registration (Program.cs)

```csharp
// Configure ETag support for cache validation
builder.Services.AddETags();
```

### 2. Middleware Pipeline (Program.cs)

```csharp
// Enable output caching middleware
app.UseOutputCache();

// Enable ETag middleware for cache validation (after output cache)
app.UseETags();
```

### 3. Endpoint Configuration

**FeatureServer Endpoints** (`FeatureServerEndpoints.cs`):
- Service metadata: `.CacheOutput("ServiceMetadata").WithETag()`
- Layer metadata: `.CacheOutput("LayerMetadata").WithETag()`
- Query endpoints: `.WithETag()` (for dynamic GeoJSON content)

**OGC API Features Endpoints** (`OgcFeaturesEndpoints.cs`):
- Landing page: `.CacheOutput("OgcLandingPage").WithETag()`
- Conformance: `.CacheOutput("OgcConformance").WithETag()`
- Collections list: `.CacheOutput("OgcCollections").WithETag()`
- Individual collections: `.CacheOutput("OgcCollection").WithETag()`
- Feature items: `.WithETag()` (for dynamic GeoJSON responses)
- Individual features: `.WithETag()`

## Cache Validation Flow

### 1. First Request
1. Client requests resource
2. Server generates content
3. ETag computed from content hash
4. Response includes `ETag` header
5. Client caches response with ETag

### 2. Subsequent Requests
1. Client sends `If-None-Match: "etag-value"`
2. Server computes current ETag
3. If ETags match → 304 Not Modified (no body)
4. If ETags differ → 200 OK with new content and ETag

### 3. Conditional Updates
1. Client sends `If-Match: "etag-value"` for updates
2. Server validates ETag matches current resource
3. If match → proceed with update
4. If no match → 412 Precondition Failed

## Performance Benefits

### 1. Bandwidth Savings
- 304 responses eliminate response body transfer
- Particularly effective for metadata endpoints that change infrequently
- Significant savings for large GeoJSON responses

### 2. Server Resource Optimization
- Reduces serialization overhead for unchanged content
- Works with output cache to avoid duplicate processing
- Efficient content hashing using SHA256

### 3. Client Experience
- Faster page loads due to cache revalidation
- Reduced data transfer for mobile clients
- Better offline experience with cached content

## ETag Computation Strategy

### 1. Metadata Endpoints
- ETags computed from JSON serialization of metadata objects
- Consistent across server instances due to deterministic serialization
- Changes automatically when underlying data changes

### 2. Dynamic Content (GeoJSON)
- ETags computed from complete JSON response
- Includes query parameters in content hash
- Efficient validation for frequently accessed spatial data

### 3. Content Hashing
- SHA256 for cryptographically strong hashes
- Base64 encoding for compact representation
- No collisions ensure reliable cache validation

## Error Handling

### 1. ETag Generation Failures
- Graceful fallback to non-ETag responses
- Logging for debugging issues
- No impact on core functionality

### 2. Conditional Request Validation
- Proper HTTP status codes (304, 412)
- Clear error messages for debugging
- Maintains compatibility with HTTP specifications

## Compatibility

### 1. HTTP Standards Compliance
- RFC 7232 compliant ETag implementation
- Proper handling of conditional headers
- Strong ETags for reliable validation

### 2. Existing Infrastructure
- Works with ASP.NET Core output caching
- Compatible with compression middleware
- Maintains existing cache policies

### 3. Client Support
- Works with all HTTP/1.1 compliant clients
- Browser cache integration
- API client libraries benefit automatically

## Future Enhancements

### 1. Weak ETags
- Consider weak ETags for content that may have minor differences
- Useful for content with acceptable variations

### 2. ETag Storage
- Consider storing ETags in cache for faster lookup
- Useful for expensive content generation scenarios

### 3. Content Negotiation
- Different ETags for different content types (JSON, HTML, etc.)
- More precise cache validation per format

## Monitoring and Debugging

### 1. Logging
- ETag generation logged at debug level
- Cache hit/miss tracking
- Performance metrics for ETag computation

### 2. Headers
- Response includes proper ETag headers
- Cache-Control headers for validation caching
- Last-Modified headers where available

### 3. Metrics
- Track 304 response rates
- Monitor ETag computation performance
- Cache effectiveness metrics

## Testing Recommendations

### 1. Integration Tests
- Test conditional GET requests
- Verify 304 Not Modified responses
- Validate ETag consistency

### 2. Performance Tests
- Measure ETag computation overhead
- Test cache hit rates
- Monitor bandwidth savings

### 3. Compatibility Tests
- Test with various HTTP clients
- Verify browser behavior
- API client compatibility

This implementation provides a robust foundation for HTTP cache validation in Honua Server, improving performance while maintaining full compatibility with existing caching infrastructure.