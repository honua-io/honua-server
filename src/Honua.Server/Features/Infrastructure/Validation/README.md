# Shared Validation Components

This directory contains reusable validation components that consolidate common validation patterns across all Honua Server protocols (GeoServices REST, OGC API Features, OData v4, MVT).

## Overview

The validation system eliminates code duplication by providing shared validation components for:

- **Query parameter validation** (limits, formats, spatial references)
- **Route parameter validation** (service IDs, layer IDs, collection IDs)
- **Spatial query validation** (bounding boxes, spatial relationships, distance queries)
- **Input sanitization** (SQL injection prevention, XSS protection)
- **Error response standardization** (consistent error formats across protocols)

## Components

### 1. CommonQueryValidator

**Purpose**: Validates common query parameters shared across all protocols.

**Services**:
- `ICommonQueryValidator` - Interface for query parameter validation
- `CommonQueryValidator` - Implementation with configurable limits

**Key Methods**:
```csharp
// Pagination validation (offset/limit parameters)
ValidationResult ValidatePagination(int? offset, int? limit)

// Format parameter validation (json, geojson, xml, html, etc.)
ValidationResult<string> ValidateFormat(string? format, ISet<string> allowedFormats)

// Spatial reference system validation
ValidationResult<int?> ValidateSrid(string? srid, string parameterName)

// Query parameter whitelist validation
ValidationResult ValidateAllowedParameters(IQueryCollection queryParameters, ISet<string> allowedParameters)

// Bounding box validation with coordinate checks
ValidationResult<BoundingBox> ValidateBbox(string? bboxValue, int targetSrid)

// WHERE clause security validation
ValidationResult ValidateWhereClause(string? whereClause)
```

### 2. RouteParameterValidator

**Purpose**: Validates and extracts route parameters with consistent error handling.

**Services**:
- `IRouteParameterValidator` - Interface for route parameter validation
- `RouteParameterValidator` - Implementation with security checks

**Key Methods**:
```csharp
// Service ID validation (alphanumeric + hyphens/underscores)
ValidationResult<string> ValidateServiceId(HttpContext context)

// Layer ID validation (non-negative integers)
ValidationResult<int> ValidateLayerId(HttpContext context)

// Collection ID validation (OGC API Features)
ValidationResult<string> ValidateCollectionId(HttpContext context)

// Feature ID validation with URL decoding
ValidationResult<string> ValidateFeatureId(HttpContext context)

// HTTP method validation with proper error responses
ValidationResult ValidateHttpMethod(HttpContext context, ISet<string> allowedMethods)

// Content type validation for request bodies
ValidationResult<string> ValidateContentType(HttpContext context, ISet<string> allowedContentTypes)
```

### 3. ValidationExtensions

**Purpose**: Extension methods for complex validation scenarios and validation chaining.

**Key Methods**:
```csharp
// Service and layer existence validation
Task<ValidationResult<(ServiceDefinition, LayerDefinition)>> ValidateServiceAndLayerAsync(
    this ILayerCatalogService catalogService, string serviceId, int layerId)

// Field existence and queryability validation
ValidationResult ValidateQueryableField(this LayerDefinition layer, string fieldName)

// Output fields validation against layer schema
ValidationResult<string[]> ValidateOutputFields(this LayerDefinition layer, string? outFields)

// Spatial relationship validation
ValidationResult<string> ValidateSpatialRelationship(string? spatialRel)

// Distance query validation
ValidationResult ValidateDistanceQuery(double? distance, string? units)

// Validation chaining for cleaner code
ValidationResult Then(this ValidationResult validationResult, Func<ValidationResult> nextValidation)
```

### 4. ValidationErrorHelpers

**Purpose**: Standardized error response creation for all protocols.

**Key Methods**:
```csharp
// GeoServices REST API error responses
IResult CreateGeoServicesValidationError(string message, string[]? details = null)

// OGC API Features error responses (RFC 7807 Problem Details)
IResult CreateOgcValidationError(string title, string detail, string? instance = null)

// OData error responses
IResult CreateODataValidationError(string code, string message)

// HTTP method not allowed responses
IResult CreateMethodNotAllowed(ISet<string> allowedMethods)

// Content type validation failures
IResult CreateUnsupportedMediaType(string receivedType, ISet<string> allowedTypes)

// Validation result helpers
IResult? CreateErrorIfInvalid(ValidationResult validationResult, Func<string, IResult> errorResponseFactory)

// Combining multiple validations
ValidationResult CombineValidationResults(params ValidationResult[] validationResults)
```

### 5. SecurityValidationAttributes

**Purpose**: Data annotation attributes for model validation (existing, enhanced).

**Attributes**:
- `[SafeSqlIdentifier]` - SQL identifier validation
- `[ValidSrid]` - Spatial reference validation
- `[AllowedFileExtension]` - File extension validation
- `[ValidCoordinate]` - Geographic coordinate validation
- `[SafeString]` - XSS prevention
- `[SafeWhereClause]` - SQL injection prevention
- `[ValidPagination]` - Pagination parameter validation

## Registration

The validation services are automatically registered in the DI container:

```csharp
// In Program.cs
builder.Services.AddValidationServices();
```

This registers:
- `ICommonQueryValidator` as Singleton
- `IRouteParameterValidator` as Singleton

## Usage Examples

### Before: Duplicated Validation in OGC Endpoint

```csharp
public static IResult HandleGetItems(HttpContext context, string collectionId)
{
    // Duplicated parameter validation
    var allowed = new HashSet<string> { "f", "bbox", "limit", "offset" };
    foreach (var key in context.Request.Query.Keys)
    {
        if (!allowed.Contains(key))
            return TypedResults.BadRequest($"Unknown parameter: {key}");
    }

    // Duplicated collection ID validation
    if (string.IsNullOrWhiteSpace(collectionId))
        return TypedResults.BadRequest("Collection ID required");

    // More inline validation...
}
```

### After: Using Shared Validation Components

```csharp
public static async Task<IResult> HandleGetItems(
    HttpContext context,
    string collectionId,
    ILayerCatalogService catalogService,
    ICommonQueryValidator queryValidator,
    IRouteParameterValidator routeValidator)
{
    // 1. Validate route parameters
    var collectionResult = routeValidator.ValidateCollectionId(context);
    var validationError = ValidationErrorHelpers.CreateErrorIfInvalid(
        collectionResult,
        error => ValidationErrorHelpers.CreateOgcValidationError("Invalid Collection", error));
    if (validationError != null) return validationError;

    // 2. Validate query parameters
    var allowedParams = new HashSet<string> { "f", "bbox", "limit", "offset" };
    var paramsResult = queryValidator.ValidateAllowedParameters(context.Request.Query, allowedParams);
    validationError = ValidationErrorHelpers.CreateErrorIfInvalid(
        paramsResult,
        error => ValidationErrorHelpers.CreateOgcValidationError("Invalid Parameter", error));
    if (validationError != null) return validationError;

    // 3. Validate collection existence
    var layerResult = await catalogService.ValidateCollectionAsync(collectionId);
    validationError = ValidationErrorHelpers.CreateErrorIfInvalid(
        layerResult,
        error => ValidationErrorHelpers.CreateOgcValidationError("Collection Not Found", error));
    if (validationError != null) return validationError;

    // All validations passed - proceed with business logic
    // ...
}
```

### Validation Chaining Example

```csharp
public static async Task<IResult> HandleFeatureServerQuery(
    HttpContext context,
    ICommonQueryValidator queryValidator,
    IRouteParameterValidator routeValidator)
{
    // Chain validations for cleaner error handling
    var serviceResult = routeValidator.ValidateServiceId(context);
    var layerResult = routeValidator.ValidateLayerId(context);

    var combinedResult = ValidationErrorHelpers.CombineValidationResults(
        serviceResult, layerResult);

    if (!combinedResult.IsValid)
    {
        return ValidationErrorHelpers.CreateGeoServicesValidationError(
            combinedResult.ErrorMessage!);
    }

    // Continue with business logic...
}
```

## Benefits

### 1. Code Reduction
- **Before**: 200+ lines of duplicated validation across 15+ endpoints
- **After**: Centralized validation with consistent behavior

### 2. Consistency
- Same validation rules across all protocols
- Consistent error message formats
- Unified parameter handling

### 3. Security
- Centralized security checks prevent bypassing
- SQL injection prevention in one place
- XSS protection consistently applied

### 4. Maintainability
- Single source of truth for validation logic
- Changes apply to all protocols automatically
- Easy to add new validation rules

### 5. Testing
- Comprehensive unit tests for all validation scenarios
- Better coverage through focused testing
- Easier to verify security properties

## Testing

All validation components have comprehensive unit tests:

- `CommonQueryValidatorTests.cs` - Query parameter validation
- `RouteParameterValidatorTests.cs` - Route parameter validation
- `ValidationExtensionsTests.cs` - Extension method validation
- `ValidationErrorHelpersTests.cs` - Error response creation

Run tests with:
```bash
dotnet test tests/Honua.Server.Tests/Infrastructure/Validation/
```

## Security Considerations

The validation system provides defense-in-depth security:

1. **Input Validation**: All user input is validated before processing
2. **SQL Injection Prevention**: WHERE clauses are checked for dangerous patterns
3. **XSS Prevention**: String inputs are sanitized
4. **Path Traversal Prevention**: File and identifier validation
5. **Rate Limiting Integration**: Works with existing rate limiting middleware

## Future Enhancements

1. **Authentication Validation**: Extract common auth patterns
2. **Custom Validation Rules**: Plugin system for domain-specific validation
3. **Performance Metrics**: Validation timing and success rates
4. **Configuration-Based Rules**: Runtime validation rule configuration