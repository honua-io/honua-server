// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Databricks.Queries.Filters;

/// <summary>
/// Translates the canonical filter AST into parameterized Databricks SQL (Spark SQL).
/// </summary>
/// <remarks>
/// <para>
/// This translator is the read-only Databricks provider's answer to canonical WHERE
/// translation: the GeoServices REST <c>where</c> clause is parsed into the shared
/// <see cref="FilterExpression"/> AST by <c>GeoServicesSqlParser</c>, then walked here to
/// emit Spark-SQL predicates whose literal operands are bound as <c>:pN</c> parameters.
/// This replaces the previous verbatim WHERE pass-through, which forwarded the raw client
/// string into the warehouse SQL without re-parsing or parameterization.
/// </para>
/// <para>
/// Identifiers are backtick-quoted through <see cref="DatabricksSqlDialect"/>. Attribute
/// predicates (comparison, logical, <c>IN</c>, <c>LIKE</c>, <c>BETWEEN</c>-as-range,
/// <c>IS [NOT] NULL</c>, arithmetic) and envelope/relationship spatial predicates are
/// supported. The scalar <c>TRIM</c> function is supported; other scalar
/// <c>FunctionCall</c>s (EXTRACT/SUBSTRING/CAST and the rest of the GeoServices SQL function
/// surface), temporal predicates, distance predicates, and cross-SRID
/// geometry literals are rejected with <see cref="NotSupportedException"/> in this slice so
/// callers never receive over-broad results.
/// </para>
/// </remarks>
internal sealed class DatabricksSqlFilterTranslator : SqlFilterExpressionVisitorBase
{
    public DatabricksSqlFilterTranslator()
        : base(DatabricksSqlDialect.Instance)
    {
    }

    protected override string TranslateBinary(BinaryExpression binary, FilterTranslationContext context)
    {
        // Empty IN/NOT IN lists short-circuit to constant predicates (mirrors the other
        // SQL translators) so the warehouse never sees an invalid empty parenthesis group.
        if (binary.Right is ValueList { Values.Count: 0 })
        {
            return binary.Operator switch
            {
                BinaryOperator.In => "FALSE",
                BinaryOperator.NotIn => "TRUE",
                _ => TranslateBinaryWithValues(binary, context),
            };
        }

        return TranslateBinaryWithValues(binary, context);
    }

    private string TranslateBinaryWithValues(BinaryExpression binary, FilterTranslationContext context)
    {
        var left = TranslateExpression(binary.Left, context);
        var right = TranslateExpression(binary.Right, context);

        var op = binary.Operator switch
        {
            BinaryOperator.And => "AND",
            BinaryOperator.Or => "OR",
            BinaryOperator.Equal => "=",
            BinaryOperator.NotEqual => "<>",
            BinaryOperator.LessThan => "<",
            BinaryOperator.LessThanOrEqual => "<=",
            BinaryOperator.GreaterThan => ">",
            BinaryOperator.GreaterThanOrEqual => ">=",
            BinaryOperator.Like => "LIKE",
            BinaryOperator.NotLike => "NOT LIKE",
            BinaryOperator.In => "IN",
            BinaryOperator.NotIn => "NOT IN",
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            _ => throw new NotSupportedException(
                $"Binary operator '{binary.Operator}' is not supported by the Databricks provider."),
        };

        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or
            or BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
            or BinaryOperator.Divide or BinaryOperator.Modulo)
        {
            return $"({left} {op} {right})";
        }

        return $"{left} {op} {right}";
    }

    protected override string TranslateUnary(UnaryExpression unary, FilterTranslationContext context)
    {
        var operand = TranslateExpression(unary.Operand, context);

        return unary.Operator switch
        {
            UnaryOperator.Not => $"NOT ({operand})",
            UnaryOperator.IsNull => $"{operand} IS NULL",
            UnaryOperator.IsNotNull => $"{operand} IS NOT NULL",
            UnaryOperator.Negate => $"-({operand})",
            _ => throw new NotSupportedException(
                $"Unary operator '{unary.Operator}' is not supported by the Databricks provider."),
        };
    }

    protected override string TranslateFunction(FunctionCall function, FilterTranslationContext context)
    {
        if (!string.Equals(function.FunctionName, "TRIM", StringComparison.OrdinalIgnoreCase))
        {
            return base.TranslateFunction(function, context);
        }

        if (function.Arguments.Count != 1)
        {
            throw new NotSupportedException(
                $"Function 'TRIM' requires exactly one argument; received {function.Arguments.Count}.");
        }

        return $"trim({TranslateExpression(function.Arguments[0], context)})";
    }

    protected override string TranslateProperty(PropertyReference property, FilterTranslationContext context)
    {
        var field = context.TryGetField(property.PropertyName);
        if (field is not null)
        {
            return Dialect.QuoteIdentifier(field.Value.Name);
        }

        if (IsGeometryAlias(property.PropertyName) && context.GeometryColumnName is { Length: > 0 } geom)
        {
            return Dialect.QuoteIdentifier(geom);
        }

        throw new ArgumentException(
            $"Field '{property.PropertyName}' is not defined on layer '{context.ResourceName}'.");
    }

    protected override string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context)
    {
        var left = TranslateGeometryExpression(spatial.Left, context);
        var right = TranslateGeometryExpression(spatial.Right, context);

        // Spark SQL spatial relationship functions (DBSQL geospatial). The provider already
        // documents that ST_* availability depends on the warehouse runtime.
        return spatial.Operator switch
        {
            SpatialOperator.Intersects => $"st_intersects({left}, {right})",
            SpatialOperator.Contains => $"st_contains({left}, {right})",
            SpatialOperator.Within => $"st_within({left}, {right})",
            SpatialOperator.Crosses => $"st_crosses({left}, {right})",
            SpatialOperator.Touches => $"st_touches({left}, {right})",
            SpatialOperator.Overlaps => $"st_overlaps({left}, {right})",
            SpatialOperator.Disjoint => $"st_disjoint({left}, {right})",
            SpatialOperator.Equals => $"st_equals({left}, {right})",
            _ => throw new NotSupportedException(
                $"Spatial operator '{spatial.Operator}' is not supported by the Databricks provider."),
        };
    }

    protected override string TranslateSpatialDistance(SpatialDistancePredicate spatial, FilterTranslationContext context)
        => throw new NotSupportedException(
            "Distance spatial filters are not supported by the Databricks provider in this slice.");

    private string TranslateGeometryExpression(FilterExpression expression, FilterTranslationContext context)
    {
        switch (expression)
        {
            case GeometryLiteral geometry:
                {
                    if (geometry.Srid != 0 && geometry.Srid != context.Wkid)
                    {
                        throw new NotSupportedException(
                            $"Cross-SRID geometry literals are not supported by the Databricks provider " +
                            $"(layer SRID is {context.Wkid}, literal SRID is {geometry.Srid}). " +
                            $"Pre-project geometries to the layer SRID before filtering.");
                    }

                    // Bind the WKB as a hex-string parameter and reconstruct the geometry in-database.
                    var hex = Convert.ToHexString(geometry.Wkb);
                    var marker = AddParameter(hex);
                    return $"st_geomfromwkb(unhex({marker}))";
                }

            case PropertyReference property:
                {
                    var field = context.TryGetField(property.PropertyName);
                    if (field is { IsGeometry: true })
                    {
                        return Dialect.QuoteIdentifier(field.Value.Name);
                    }

                    if (field is null && IsGeometryAlias(property.PropertyName) && context.GeometryColumnName is { Length: > 0 } geom)
                    {
                        return Dialect.QuoteIdentifier(geom);
                    }

                    throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field.");
                }

            default:
                throw new NotSupportedException(
                    $"Geometry expression '{expression.GetType().Name}' is not supported by the Databricks provider.");
        }
    }

    private static bool IsGeometryAlias(string propertyName)
        => propertyName.Equals("geom", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("shape", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("geometry", StringComparison.OrdinalIgnoreCase);
}
