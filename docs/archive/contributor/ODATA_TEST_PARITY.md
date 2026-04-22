# OData v4 Test Parity Matrix

This document maps OData v4 test coverage against OGC API Features Python test matrices, as required by issue #200.

## Overview

The OData test suite provides comprehensive coverage of OData v4 specification features, aligned with existing OGC API Features test patterns for consistency across protocols.

## Test File Summary

| Test File | Purpose | Test Count |
|-----------|---------|------------|
| `ODataEndpointTests.cs` | Basic endpoint behavior, headers, metadata | ~25 |
| `ODataCrudEndpointTests.cs` | Create, Update, Delete operations | ~5 |
| `ODataAdvancedFeaturesTests.cs` | Batch, Aggregation, Search, Expand | ~30 |
| `ODataClientIntegrationTests.cs` | OData client library integration | ~40 |
| `ODataSpatialReferenceTests.cs` | SRID transformation | ~1 |
| `ODataFilterMatrixTests.cs` | Comprehensive filter coverage | ~35 |
| `ODataSpatialMatrixTests.cs` | Spatial predicate coverage | ~20 |
| `ODataGeometryCrudTests.cs` | CRUD with geometry types | ~15 |
| `ODataPaginationTests.cs` | Pagination and nextLink validation | ~30 |
| `ODataErrorHandlingTests.cs` | Error scenarios and validation | ~35 |

**Total: ~236 tests**

## Filter Matrix Coverage

### Comparison Operators

| Operator | OData Syntax | Test Coverage |
|----------|-------------|---------------|
| Equal | `eq` | `ODataFilterMatrixTests.Filter_EqualInteger_ReturnsSingleMatch` |
| Not Equal | `ne` | `ODataFilterMatrixTests.Filter_NotEqualInteger_ExcludesMatch` |
| Greater Than | `gt` | `ODataFilterMatrixTests.Filter_GreaterThan_ReturnsLargeCities` |
| Greater Than or Equal | `ge` | `ODataFilterMatrixTests.Filter_GreaterThanOrEqual_IncludesBoundary` |
| Less Than | `lt` | `ODataFilterMatrixTests.Filter_LessThan_ReturnsSmallCities` |
| Less Than or Equal | `le` | `ODataFilterMatrixTests.Filter_LessThanOrEqual_IncludesBoundary` |

### String Functions

| Function | OData Syntax | Test Coverage |
|----------|-------------|---------------|
| Contains | `contains(field, 'value')` | `ODataFilterMatrixTests.Filter_Contains_ReturnsPartialMatches` |
| Starts With | `startswith(field, 'value')` | `ODataFilterMatrixTests.Filter_StartsWith_ReturnsPrefix` |
| Ends With | `endswith(field, 'value')` | `ODataFilterMatrixTests.Filter_EndsWith_ReturnsSuffix` |
| Substring | `substring(field, start, length)` | `ODataErrorHandlingTests.Filter_SubstringFunction_ReturnsMatches` |
| To Lower | `tolower(field)` | `ODataErrorHandlingTests.Filter_ToLowerFunction_ReturnsMatches` |
| Concat | `concat(field1, field2)` | `ODataErrorHandlingTests.Filter_ConcatFunction_ReturnsMatches` |

### Date/Time Functions

| Function | OData Syntax | Test Coverage |
|----------|-------------|---------------|
| Year | `year(datetime'2020-01-01T00:00:00Z')` | `ODataErrorHandlingTests.Filter_YearFunction_ReturnsMatches` |

### Field Type Handling

| Type | Test Coverage |
|------|---------------|
| Integer | `Filter_EqualInteger_ReturnsSingleMatch` |
| String | `Filter_EqualString_ReturnsSingleMatch` |
| Boolean | `Filter_EqualBooleanTrue_ReturnsCapitals` |
| Double | `Filter_EqualDouble_ReturnsExactMatch` |
| Null | `Filter_EqualNull_ReturnsNullFields` |

### Logical Operators

| Operator | OData Syntax | Test Coverage |
|----------|-------------|---------------|
| And | `... and ...` | `Filter_AndOperator_RequiresBothConditions` |
| Or | `... or ...` | `Filter_OrOperator_MatchesEitherCondition` |
| Multiple And | `... and ... and ...` | `Filter_MultipleAnd_RequiresAllConditions` |
| Multiple Or | `... or ... or ...` | `Filter_MultipleOr_MatchesAnyCondition` |
| Nested Parentheses | `(... and ...) or (...)` | `Filter_MixedLogicalWithParentheses_GroupsCorrectly` |

## Spatial Matrix Coverage

### geo.intersects Function

| Geometry Type | Test Coverage |
|---------------|---------------|
| Point in Polygon | `GeoIntersects_PolygonContainingSinglePoint_ReturnsSingleFeature` |
| Multiple Points in Polygon | `GeoIntersects_PolygonContainingMultiplePoints_ReturnsMultipleFeatures` |
| Points outside Polygon | `GeoIntersects_PolygonExcludingPoints_ReturnsNoFeatures` |
| Point at Exact Location | `GeoIntersects_PointAtExactLocation_ReturnsSingleFeature` |
| With Attribute Filter | `GeoIntersects_CombinedWithAttributeFilter_ReturnsCombinedResult` |
| SRID Transformation (4326 to 3857) | `GeoIntersects_OnSrid3857Layer_TransformsFilterToLayerSrid` |

### geo.distance Function

| Comparison | Test Coverage |
|------------|---------------|
| Less Than | `GeoDistance_LessThan_ReturnsNearbyFeatures` |
| Less Than or Equal | `GeoDistance_LessThanOrEqual_IncludesBoundary` |
| Greater Than | `GeoDistance_GreaterThan_ExcludesNearbyFeatures` |
| Greater Than or Equal | `GeoDistance_GreaterThanOrEqual_IncludesBoundary` |
| Combined with Attribute | `GeoDistance_CombinedWithPopulationFilter_ReturnsCombinedResult` |

### Null Geometry Handling

| Scenario | Test Coverage |
|----------|---------------|
| Exclude null geometries from spatial | `GeoIntersects_WithNullGeometry_ExcludesNullGeometries` |
| Filter for null geometry | `Filter_GeometryEqualsNull_ReturnsNullGeometries` |
| Filter for non-null geometry | `Filter_GeometryNotEqualsNull_ExcludesNullGeometries` |

## CRUD Geometry Coverage

### Create (POST)

| Scenario | Test Coverage |
|----------|---------------|
| With Point Geometry | `CreateFeature_WithPointGeometry_ReturnsCreatedWithGeometry` |
| Without Geometry | `CreateFeature_WithoutGeometry_ReturnsCreatedWithNullGeometry` |
| Attributes Only | `CreateFeature_WithAttributesOnly_ReturnsCreated` |
| SRID Transformation | `CreateFeature_OnSrid3857Layer_TransformsGeometry` |
| Non-existent Layer | `CreateFeature_NonExistentLayer_ReturnsNotFound` |

### Update (PATCH)

| Scenario | Test Coverage |
|----------|---------------|
| Update Geometry | `UpdateFeature_WithNewGeometry_ReturnsUpdatedGeometry` |
| Update Attributes Only | `UpdateFeature_AttributesOnly_PreservesGeometry` |
| Set Geometry to Null | `UpdateFeature_SetGeometryToNull_ClearsGeometry` |
| Non-existent Feature | `UpdateFeature_NonExistent_ReturnsNotFound` |

### Delete (DELETE)

| Scenario | Test Coverage |
|----------|---------------|
| With Geometry | `DeleteFeature_WithGeometry_ReturnsNoContent` |
| Without Geometry | `DeleteFeature_WithoutGeometry_ReturnsNoContent` |
| Non-existent | `DeleteFeature_NonExistent_ReturnsNotFound` |

## Pagination Coverage

### $top Parameter

| Scenario | Test Coverage |
|----------|---------------|
| $top=1 | `Top_One_ReturnsSingleResult` |
| $top=0 | `Top_Zero_ReturnsEmptyResults` |
| $top exceeds total | `Top_ExceedsTotalCount_ReturnsAllResults` |
| $top within range | `Top_WithinRange_ReturnsExactCount` |

### $skip Parameter

| Scenario | Test Coverage |
|----------|---------------|
| $skip=0 | `Skip_Zero_ReturnsAllFromBeginning` |
| $skip partial | `Skip_PartialOffset_ReturnsRemainingResults` |
| $skip=total | `Skip_ExactTotalCount_ReturnsEmptyResults` |
| $skip exceeds total | `Skip_ExceedsTotalCount_ReturnsEmptyResults` |

### $count Parameter

| Scenario | Test Coverage |
|----------|---------------|
| $count=true | `Count_True_ReturnsTotalCount` |
| $count=false | `Count_False_DoesNotIncludeCount` |
| $top with $count | `TopWithCount_ReturnsTotalCountNotLimited` |
| $filter with $count | `FilterWithCount_ReturnsFilteredCount` |

### nextLink Validation

| Scenario | Test Coverage |
|----------|---------------|
| Present when more results | `NextLink_WhenMoreResultsExist_ReturnsValidNextLink` |
| Following nextLink | `NextLink_FollowNextLink_ReturnsNextPage` |
| Iterate all pages | `NextLink_IterateAllPages_ReturnsAllFeatures` |
| Absent on last page | `NextLink_LastPage_NoNextLink` |
| Preserves $filter | `NextLink_WithFilter_PreservesFilterInNextLink` |
| Preserves $orderby | `NextLink_WithOrderBy_PreservesOrderByInNextLink` |

## Error Handling Coverage

### Invalid Filter Syntax

| Error Type | Test Coverage |
|------------|---------------|
| Invalid syntax | `Filter_InvalidSyntax_ReturnsBadRequest` |
| Missing operand | `Filter_MissingLeftOperand_ReturnsBadRequest` |
| Invalid operator | `Filter_InvalidOperator_ReturnsBadRequest` |
| Unbalanced parentheses | `Filter_UnbalancedParentheses_ReturnsBadRequest` |
| Unclosed string | `Filter_UnclosedStringLiteral_ReturnsBadRequest` |
| Non-existent field | `Filter_NonExistentField_ReturnsBadRequest` |

### Invalid Query Parameters

| Error Type | Test Coverage |
|------------|---------------|
| Negative $top | `Top_NegativeValue_ReturnsBadRequest` |
| Negative $skip | `Skip_NegativeValue_ReturnsBadRequest` |
| Non-numeric $top | `Top_NonNumericValue_ReturnsBadRequest` |
| Invalid $orderby field | `OrderBy_InvalidField_ReturnsBadRequest` |

### Previously Unsupported Functions

| Function | Test Coverage |
|----------|---------------|
| substring() | `Filter_SubstringFunction_ReturnsMatches` |
| year() | `Filter_YearFunction_ReturnsMatches` |
| tolower() | `Filter_ToLowerFunction_ReturnsMatches` |
| concat() | `Filter_ConcatFunction_ReturnsMatches` |

### Malformed Geometry

| Error Type | Test Coverage |
|------------|---------------|
| Invalid WKT | `GeoIntersects_MalformedGeometry_ReturnsBadRequest` |
| Incomplete polygon | `GeoIntersects_IncompletePolygon_ReturnsBadRequest` |
| Unclosed polygon | `GeoIntersects_UnclosedPolygon_ReturnsBadRequest` |
| Malformed point | `GeoDistance_MalformedPoint_ReturnsBadRequest` |
| Non-numeric coordinates | `GeoDistance_NonNumericCoordinates_ReturnsBadRequest` |

### Resource Not Found

| Error Type | Test Coverage |
|------------|---------------|
| Non-existent layer | `GetFeatures_NonExistentLayer_ReturnsNotFound` |
| Non-existent feature | `GetFeature_NonExistent_ReturnsNotFound` |

### OData Error Format

| Requirement | Test Coverage |
|-------------|---------------|
| error object present | `Error_HasCorrectODataV4Format` |
| code property | `Error_HasCorrectODataV4Format` |
| message property | `Error_HasCorrectODataV4Format` |

## OGC API Features Parity

This section maps OData tests to equivalent OGC API Features test scenarios.

### Filter Parity

| OGC Filter | OData Equivalent | Status |
|------------|------------------|--------|
| CQL `property = value` | `$filter=property eq value` | Covered |
| CQL `property <> value` | `$filter=property ne value` | Covered |
| CQL `property > value` | `$filter=property gt value` | Covered |
| CQL `property LIKE '%value%'` | `$filter=contains(property, 'value')` | Covered |
| CQL `INTERSECTS(geometry, polygon)` | `$filter=geo.intersects(Geometry, geography'POLYGON(...)')` | Covered |
| CQL `DWITHIN(geometry, point, distance)` | `$filter=geo.distance(Geometry, geography'POINT(...)') lt distance` | Covered |

### Pagination Parity

| OGC Parameter | OData Equivalent | Status |
|---------------|------------------|--------|
| `limit` | `$top` | Covered |
| `offset` | `$skip` | Covered |
| `next` link | `@odata.nextLink` | Covered |
| `numberMatched` | `@odata.count` | Covered |

### CRUD Parity

| OGC Operation | OData Equivalent | Status |
|---------------|------------------|--------|
| POST /collections/{id}/items | POST /odata/Features({layerId}) | Covered |
| PUT /collections/{id}/items/{id} | PATCH /odata/Features({layerId},{objectId}) | Covered |
| DELETE /collections/{id}/items/{id} | DELETE /odata/Features({layerId},{objectId}) | Covered |

## Running the Tests

```bash
# Run all OData tests
dotnet test tests/dotnet/Honua.Server.Tests/Features/OData/

# Run specific test file
dotnet test tests/dotnet/Honua.Server.Tests/Features/OData/ODataFilterMatrixTests.cs

# Run tests with filter
dotnet test --filter "FullyQualifiedName~OData"

# Run tests with verbose output
dotnet test tests/dotnet/Honua.Server.Tests/Features/OData/ --logger "console;verbosity=detailed"
```

## Test Data

Tests use the seed file at `tests/seed/odata.yaml` which provides:

- **15 US Cities** with attributes: name, population, area_sq_km, is_capital, state, country, founded_year, rating, notes
- **6 City Landmarks** for expand/relationship testing
- Point geometries in WGS84 (SRID 4326)
- One city (Virtual City) with null geometry for null handling tests

For SRID transformation tests, `tests/seed/spatial-reference.yaml` provides layers with SRID 3857.
