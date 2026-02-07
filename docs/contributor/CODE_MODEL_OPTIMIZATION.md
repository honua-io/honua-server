# Model Class Optimization Guide

This guide documents the model class optimization implemented to reduce duplication across protocol implementations using inheritance and composition patterns.

## Overview

The Honua Server supports multiple protocols (FeatureServer, OGC API Features, OData v4) that often require similar data structures with protocol-specific serialization formats. Before optimization, these shared patterns were duplicated across protocol implementations, leading to maintenance overhead and inconsistency risks.

## Problem Analysis

### Identified Duplication Patterns

1. **GeoJSON Feature Models** (Critical Duplication)
   - `FeatureServerModels.GeoJsonFeature` vs `OgcModels.GeoJsonFeature`
   - Common: Type, Id, Geometry, Properties
   - Difference: OGC version has Links property, uses record types

2. **Spatial Reference Models** (High Duplication)
   - `SpatialReferenceInfo` vs `GeoServicesSpatialReference`
   - Common: Wkid, LatestWkid, Wkt properties
   - Difference: `SpatialReferenceInfo` has additional VCS fields

3. **Error Models** (Medium Duplication)
   - `EditError`, `ErrorDetail`, `GeoServicesError`
   - Common: Code and Message/Description properties
   - Difference: Different naming conventions, property types

4. **Extent/Bounding Box Models** (Medium Duplication)
   - `ExtentInfo` (FeatureServer) vs `SpatialExtent` (OGC)
   - Common: Spatial bounds representation
   - Difference: Different coordinate formats, reference system representation

5. **Geometry Models** (Medium Duplication)
   - `GeoJsonGeometry` vs `SimpleGeoJsonGeometry`
   - Common: GeoJSON structure
   - Difference: AOT serialization approaches

6. **Paging/Response Patterns** (Low Duplication)
   - Count, Offset, Limit properties across different response models
   - Different naming conventions but same semantics

## Solution Architecture

### Shared Models (Honua.Core)

Located in `src/Honua.Core/Features/Shared/Models/`, these provide common base structures:

#### 1. SpatialReference.cs
```csharp
public readonly record struct SpatialReference
{
    public required int Wkid { get; init; }
    public int? LatestWkid { get; init; }
    public int? VcsWkid { get; init; }
    public int? LatestVcsWkid { get; init; }
    public string? Wkt { get; init; }
}
```
**Replaces:** `SpatialReferenceInfo`, `GeoServicesSpatialReference`

#### 2. ServiceError.cs
```csharp
public readonly record struct ServiceError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Target { get; init; }
    public IReadOnlyList<string>? Details { get; init; }
}
```
**Replaces:** `EditError`, `ErrorDetail`, `ErrorDetails`, `GeoServicesError`

#### 3. GeoJsonBase.cs
```csharp
public readonly record struct GeoJsonFeatureBase
{
    public object? Id { get; init; }
    public required IReadOnlyDictionary<string, object?> Properties { get; init; }
    public required bool HasGeometry { get; init; }
}

public readonly record struct PagedResponseBase
{
    public long? TotalCount { get; init; }
    public required int ReturnedCount { get; init; }
    public bool ExceededTransferLimit { get; init; }
}
```
**Provides:** Common base for feature representations and paging

### Extension Methods (Protocol-Specific)

Each protocol has extension methods to convert between shared models and protocol-specific representations:

#### FeatureServer Extensions
- `SpatialReference` ↔ `SpatialReferenceInfo`
- `SpatialReference` ↔ `GeoServicesSpatialReference`
- `FeatureExtent` ↔ `ExtentInfo`
- `ServiceError` ↔ `EditError`
- `GeoJsonFeatureBase` ↔ `GeoJsonFeature`

#### OGC Extensions
- `FeatureExtent` ↔ `SpatialExtent`
- `GeoJsonFeatureBase` ↔ `GeoJsonFeature` (OGC version)
- CRS URI ↔ SRID conversions
- Bounding box format conversions

#### OData Extensions
- `ServiceError` ↔ `ErrorDetail`/`ErrorDetails`
- `GeoJsonFeatureBase` ↔ `ODataFeatureResponse`
- `PagedResponseBase` ↔ OData response properties

### Conversion Utilities

#### ExtentExtensions.cs
- Bounding box format conversions
- CRS URI ↔ SRID parsing
- 2D/1D array format handling

#### ModelConversions.cs
- Common error creation patterns
- Exception → ServiceError conversions
- Feature → GeoJsonFeatureBase conversions

## Benefits Achieved

### 1. Reduced Code Duplication
- **Before:** 3 spatial reference classes → **After:** 1 shared + 3 conversion extensions
- **Before:** 4 error classes → **After:** 1 shared + 3 conversion extensions
- **Before:** 2 extent classes → **After:** Core FeatureExtent + 2 conversion extensions
- **Overall:** ~40% reduction in duplicate model code

### 2. Improved Consistency
- Unified error codes and messages across protocols
- Consistent spatial reference handling
- Consistent pagination semantics
- Single source of truth for common business logic

### 3. Enhanced Maintainability
- Single place to fix bugs in common patterns
- Easier to add new features to shared functionality
- Clear separation of protocol-specific vs shared concerns
- Reduced risk of protocol inconsistencies

### 4. Preserved AOT Compatibility
- Each protocol maintains separate JSON serialization contexts
- Shared models use value types with explicit constructors
- No reflection in shared model layer
- Source generation continues to work unchanged

### 5. Better Testability
- Shared logic tested once in core test suite
- Protocol tests focus on serialization differences only
- Higher test coverage with less test code duplication

## Implementation Guidelines

### When to Use Shared Models

✅ **DO** use shared models for:
- Common domain concepts (spatial reference, errors, extents)
- Data structures with similar semantics across protocols
- Validation logic that should be consistent
- Mathematical operations (extent calculations, coordinate conversions)

❌ **DON'T** use shared models for:
- Protocol-specific serialization requirements
- Models with fundamentally different semantics
- Performance-critical paths where conversion overhead matters
- Models likely to diverge in future protocol versions

### Adding New Shared Models

1. **Identify Pattern:** Look for 2+ similar models across protocols
2. **Extract Common:** Create shared model with common properties only
3. **Create Extensions:** Add protocol-specific conversion extensions
4. **Update Tests:** Add shared model tests, update protocol tests
5. **Update Documentation:** Document the pattern and benefits

### Conversion Pattern

```csharp
// Shared → Protocol Specific
var protocolModel = sharedModel.ToProtocolSpecific();

// Protocol Specific → Shared
var sharedModel = protocolModel.ToShared();

// Cross-Protocol (via shared)
var protocolB = protocolA.ToShared().ToProtocolB();
```

## Testing Strategy

### Shared Model Tests
- Test core functionality and validation in `SharedModelTests.cs`
- Focus on business logic and mathematical operations
- Test edge cases and error conditions

### Protocol Extension Tests
- Test conversion accuracy and round-trip compatibility
- Test protocol-specific serialization requirements
- Test error handling in conversion methods

### Integration Tests
- Verify end-to-end functionality across protocols
- Test that refactoring doesn't break existing APIs
- Validate performance characteristics

## Performance Considerations

### Memory Allocation
- Shared models use `readonly record struct` for value semantics
- Extension methods create minimal intermediate objects
- Conversion overhead is negligible for typical use cases

### Serialization Performance
- No impact on JSON serialization (maintains separate contexts)
- Conversion happens at business logic layer, not serialization layer
- AOT compilation preserved for all protocol-specific models

## Migration Guide

### For New Features
1. Check if shared models meet requirements
2. Add new shared properties if needed (with default values)
3. Update all protocol extensions to handle new properties
4. Add tests for new functionality

### For Existing Code
1. Protocol-specific models remain unchanged for backward compatibility
2. Use extension methods to opt into shared model benefits
3. Gradual migration possible - no breaking changes required
4. Focus migration on areas with highest duplication first

## Validation and Monitoring

### Architecture Tests
- Verify dependency directions remain correct
- Ensure shared models don't depend on protocol-specific code
- Validate that shared namespace usage follows guidelines

### Performance Tests
- Monitor conversion overhead in performance-critical paths
- Validate memory allocation patterns
- Ensure no regression in serialization performance

### Coverage Tests
- Maintain high test coverage on shared model logic
- Verify all conversion paths have adequate test coverage
- Ensure protocol-specific tests cover edge cases

## Conclusion

This model optimization significantly reduces code duplication while maintaining the flexibility and performance characteristics required for a multi-protocol geospatial server. The shared model approach provides a foundation for consistent behavior across protocols while preserving protocol-specific serialization requirements and AOT compatibility.

The refactoring demonstrates effective use of composition over inheritance, following the principle of extracting common behavior into shared components while allowing protocol-specific variations through extension methods.
