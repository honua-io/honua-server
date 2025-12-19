# ADR-0009: Shared Filter AST for Multi-Protocol Support

## Status

Accepted

## Context

Honua supports multiple query protocols, each with its own filter syntax:

| Protocol | Filter Syntax | Example |
|----------|--------------|---------|
| **FeatureServer REST** | Esri WHERE clause | `population > 1000 AND state = 'CA'` |
| **OGC API Features** | CQL2-Text | `population > 1000 AND state = 'CA'` |
| **OData v4** | $filter | `population gt 1000 and state eq 'CA'` |

Each syntax has different:
- Operators (`>` vs `gt`)
- String quoting (`'` vs `"`)
- Spatial predicates (`esriSpatialRelIntersects` vs `S_INTERSECTS` vs `geo.intersects`)
- Null handling (`IS NULL` vs `null` vs `eq null`)

Without a shared representation, we would need:
- 3 separate SQL translators (one per protocol)
- 3 separate validation implementations
- Duplicated spatial operation logic
- Inconsistent behavior across protocols

The legacy Honua codebase solved this with a unified filter infrastructure that should be preserved.

## Decision

Implement a **shared Filter AST (Abstract Syntax Tree)** in `Honua.Core` that all protocol parsers produce and all SQL translators consume.

### Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     Protocol Layer                               │
├─────────────────┬─────────────────┬─────────────────────────────┤
│ EsriWhereParser │ Cql2TextParser  │ ODataFilterParser           │
│ (FeatureServer) │ (OGC Features)  │ (OData v4)                  │
└────────┬────────┴────────┬────────┴──────────────┬──────────────┘
         │                 │                       │
         ▼                 ▼                       ▼
┌─────────────────────────────────────────────────────────────────┐
│                    FilterExpression (AST)                        │
│  Honua.Core/Queries/Filters/                                    │
└─────────────────────────────────┬───────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│                    SqlFilterTranslator                           │
│  Produces parameterized SQL WHERE clauses                        │
└─────────────────────────────────┬───────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────┐
│             PostgresSpatialTranslator                            │
│  ST_Intersects, ST_Contains, ST_Within, ST_Distance              │
└─────────────────────────────────────────────────────────────────┘
```

### Filter AST Types

```csharp
// Honua.Core/Queries/Filters/FilterExpression.cs
namespace Honua.Core.Queries.Filters;

/// <summary>Base class for all filter expressions</summary>
public abstract record FilterExpression;

/// <summary>Binary operation: AND, OR, =, <>, <, >, <=, >=, LIKE, IN</summary>
public sealed record BinaryExpression(
    FilterExpression Left,
    BinaryOperator Operator,
    FilterExpression Right) : FilterExpression;

/// <summary>Unary operation: NOT, IS NULL, IS NOT NULL</summary>
public sealed record UnaryExpression(
    UnaryOperator Operator,
    FilterExpression Operand) : FilterExpression;

/// <summary>Reference to a feature property/field</summary>
public sealed record PropertyReference(string PropertyName) : FilterExpression;

/// <summary>Literal value: string, number, boolean, null</summary>
public sealed record Literal(object? Value, LiteralType Type) : FilterExpression;

/// <summary>Spatial predicate: INTERSECTS, CONTAINS, WITHIN, CROSSES, etc.</summary>
public sealed record SpatialPredicate(
    SpatialOperator Operator,
    PropertyReference GeometryProperty,
    GeometryLiteral Geometry) : FilterExpression;

/// <summary>Geometry literal in WKB format (normalized from WKT, GeoJSON, Esri JSON)</summary>
public sealed record GeometryLiteral(
    byte[] Wkb,
    int Srid,
    string OriginalFormat) : FilterExpression;

/// <summary>Function call: UPPER, LOWER, LENGTH, etc.</summary>
public sealed record FunctionCall(
    string FunctionName,
    IReadOnlyList<FilterExpression> Arguments) : FilterExpression;

/// <summary>List of values for IN operator</summary>
public sealed record ValueList(IReadOnlyList<Literal> Values) : FilterExpression;
```

### Operators

```csharp
public enum BinaryOperator
{
    // Logical
    And, Or,

    // Comparison
    Equal, NotEqual,
    LessThan, LessThanOrEqual,
    GreaterThan, GreaterThanOrEqual,

    // String
    Like, NotLike,

    // Collection
    In, NotIn
}

public enum UnaryOperator
{
    Not,
    IsNull,
    IsNotNull
}

public enum SpatialOperator
{
    Intersects,
    Contains,
    Within,
    Crosses,
    Touches,
    Overlaps,
    Disjoint,
    Equals,

    // Distance-based
    DWithin,    // Within distance
    Beyond      // Beyond distance
}
```

### SQL Translation

```csharp
// Honua.Core/Queries/Filters/SqlFilterTranslator.cs
public sealed class SqlFilterTranslator
{
    public SqlFragment Translate(FilterExpression filter, LayerDefinition layer)
    {
        return filter switch
        {
            BinaryExpression bin => TranslateBinary(bin, layer),
            UnaryExpression un => TranslateUnary(un, layer),
            PropertyReference prop => TranslateProperty(prop, layer),
            Literal lit => TranslateLiteral(lit),
            SpatialPredicate spatial => TranslateSpatial(spatial, layer),
            FunctionCall func => TranslateFunction(func, layer),
            _ => throw new NotSupportedException($"Unknown filter type: {filter.GetType()}")
        };
    }

    private SqlFragment TranslateSpatial(SpatialPredicate spatial, LayerDefinition layer)
    {
        var geomColumn = layer.GeometryField;
        var function = spatial.Operator switch
        {
            SpatialOperator.Intersects => "ST_Intersects",
            SpatialOperator.Contains => "ST_Contains",
            SpatialOperator.Within => "ST_Within",
            SpatialOperator.Crosses => "ST_Crosses",
            SpatialOperator.Touches => "ST_Touches",
            SpatialOperator.Overlaps => "ST_Overlaps",
            SpatialOperator.Disjoint => "ST_Disjoint",
            SpatialOperator.Equals => "ST_Equals",
            _ => throw new NotSupportedException()
        };

        return new SqlFragment(
            $"{function}({geomColumn}, ST_GeomFromWKB(@p{_paramIndex}, @p{_paramIndex + 1}))",
            [spatial.Geometry.Wkb, spatial.Geometry.Srid]);
    }
}

public record SqlFragment(string Sql, IReadOnlyList<object?> Parameters);
```

## Consequences

### Benefits

1. **Single source of truth** for filter semantics
2. **Consistent behavior** across all protocols
3. **Testable in isolation** - AST can be unit tested without HTTP or SQL
4. **Extensible** - new protocols just need a parser, reuse translation
5. **Security** - parameterized SQL generated in one place

### Trade-offs

1. **Normalization overhead** - All formats convert to/from AST
2. **Lowest common denominator** - Some protocol-specific features may not map cleanly
3. **Learning curve** - Contributors must understand AST structure

### Migration Path

If protocol-specific behavior is ever needed:
- Add protocol-aware flags to AST nodes
- Or implement protocol-specific translator overrides

## References

- Legacy implementation: `../Honua.Server/src/platform/core/Query/Filter/`
- OGC CQL2 spec: https://docs.ogc.org/is/21-065r2/21-065r2.html
- OData filter spec: https://docs.oasis-open.org/odata/odata/v4.01/odata-v4.01-part2-url-conventions.html
