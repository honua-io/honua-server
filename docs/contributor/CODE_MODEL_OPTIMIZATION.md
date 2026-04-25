# Model Class Optimization Guide

This guide documents the model class optimization implemented to reduce duplication across protocol implementations using inheritance and composition patterns.

## Overview

The Honua Server supports multiple protocols (FeatureServer, MapServer, OGC API Features, OData v4) that often require similar data structures with protocol-specific serialization formats. Before optimization, these shared patterns were duplicated across protocol implementations, leading to maintenance overhead and inconsistency risks.

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

## Key Files

| File | Purpose |
|---|---|
| `src/Honua.Core/Features/Shared/Models/GeoJsonBase.cs` | `GeoJsonFeatureBase`, `PagedResponseBase` |
| `src/Honua.Core/Features/Shared/Models/ModelConversions.cs` | Exception → ServiceError, Feature → GeoJsonFeatureBase |
| `src/Honua.Server/Features/Protocols/GeoServices/FeatureServer/Models/FeatureServerExtensions.cs` | FeatureServer conversions |
| `src/Honua.Server/Features/Protocols/Ogc/Api/Features/Models/OgcExtensions.cs` | OGC conversions |
| `src/Honua.Server/Features/Protocols/OData/Models/ODataExtensions.cs` | OData conversions |
