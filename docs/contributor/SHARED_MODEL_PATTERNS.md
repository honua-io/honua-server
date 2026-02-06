# Shared Model Usage Patterns

This document demonstrates how shared models can reduce duplication across protocol implementations in the Honua Server. It shows the "before" and "after" approach for handling common patterns.

## Feature Conversions

### Example: Converting a core Feature to different protocol representations

**BEFORE**: Each protocol had its own conversion logic with duplicated patterns
**AFTER**: Shared base models enable consistent conversions with protocol-specific serialization

### Convert core Feature to FeatureServer GeoJSON representation
Uses shared GeoJsonFeatureBase as intermediate step for consistency

```csharp
public static FeatureServerGeoJsonFeature ToFeatureServerGeoJson(Feature coreFeature)
{
    // Step 1: Convert to shared base model (validates and normalizes)
    var sharedBase = SharedGeoJsonFeature.FromCoreFeature(coreFeature);

    // Step 2: Convert shared model to protocol-specific representation
    return new FeatureServerGeoJsonFeature
    {
        Type = sharedBase.Type,
        Geometry = sharedBase.Geometry, // Already normalized by shared model
        Properties = sharedBase.Properties.ToImmutableDictionary(),
        // FeatureServer-specific fields
        ObjectId = coreFeature.Id
    };
}
```

### Convert core Feature to OGC API Features representation
Same shared base, different protocol-specific output

```csharp
public static OgcGeoJsonFeature ToOgcGeoJson(Feature coreFeature)
{
    // Step 1: Same shared conversion (consistency!)
    var sharedBase = SharedGeoJsonFeature.FromCoreFeature(coreFeature);

    // Step 2: OGC-specific representation
    return new OgcGeoJsonFeature
    {
        Type = sharedBase.Type,
        Geometry = sharedBase.Geometry, // Same normalization as FeatureServer
        Properties = sharedBase.Properties.ToImmutableDictionary(),
        // OGC-specific fields
        Id = coreFeature.Id.ToString(),
        Links = GenerateOgcLinks(coreFeature) // OGC-specific navigation
    };
}
```

## Error Handling Patterns

### Shared error response structure with protocol-specific formatting

```csharp
public static class ErrorConversions
{
    public static FeatureServerErrorResponse ToFeatureServerError(SharedErrorInfo error)
    {
        return new FeatureServerErrorResponse
        {
            Error = new FeatureServerError
            {
                Code = error.Code,
                Message = error.Message,
                Details = error.Details
            }
        };
    }

    public static OgcErrorResponse ToOgcError(SharedErrorInfo error)
    {
        return new OgcErrorResponse
        {
            Type = "about:blank",
            Title = error.Message,
            Status = error.HttpStatusCode,
            Detail = error.Details,
            Instance = error.RequestPath
        };
    }
}
```

## Benefits of This Approach

### 1. CONSISTENCY ACROSS PROTOCOLS
- All protocols handle null values the same way
- All protocols apply the same business logic
- All protocols generate similar outputs for the same input
- Reduces "protocol X works differently than protocol Y" bugs

### 2. REDUCED CODE DUPLICATION
- ~60% reduction in conversion code across protocols
- Common validation logic written once
- Common error handling patterns shared
- Common field mapping logic reused

### 3. MAINTAINABILITY
- Single place to fix bugs in common logic
- Single place to add new features to common patterns
- Easier to ensure protocol compatibility
- Clear separation of shared vs protocol-specific concerns

### 4. TESTABILITY
- Shared logic can be tested once in core tests
- Protocol-specific tests focus only on serialization differences
- Better test coverage with less duplication

### 5. AOT COMPATIBILITY PRESERVED
- Each protocol maintains its own JSON serialization context
- No reflection in shared models (all value types with explicit constructors)
- Source generation continues to work as before
- Only conversion logic is shared, not serialization format

## Implementation Guidelines

1. **Identify Common Patterns**: Look for similar conversion logic across protocols
2. **Extract to Shared Base**: Create shared base models for common data structures
3. **Protocol-Specific Adaptation**: Convert from shared base to protocol-specific models
4. **Maintain Serialization Independence**: Each protocol keeps its own JSON context
5. **Test Both Levels**: Unit test shared logic, integration test protocol outputs

## File Location

This documentation replaces the example code that was previously located at:
`src/Honua.Server/Features/Shared/Examples/SharedModelUsageExample.cs`