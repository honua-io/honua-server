// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Queries.Filters;

/// <summary>
/// Translates filter expressions (CQL2/OGC AST) into MySQL/MariaDB-compatible SQL fragments.
/// Identifiers are backtick-quoted, parameters are named <c>@p0, @p1, ...</c>, and spatial
/// predicates use the canonical MySQL spatial functions. Cross-SRID geometry literals throw
/// <see cref="NotSupportedException"/>; KNN is unsupported.
/// </summary>
internal sealed class MySqlSqlFilterTranslator : ISqlFilterTranslator
{
    private const int MaxExpressionDepth = FilterExpressionNormalizer.MaxExpressionDepth;

    private int _paramIndex;
    private int _depth;
    private readonly List<object?> _parameters = [];

    /// <inheritdoc />
    public SqlFragment Translate(FilterExpression filter, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(layer);

        _paramIndex = 0;
        _depth = 0;
        _parameters.Clear();

        var sql = TranslateExpression(filter, layer);
        return new SqlFragment(sql, _parameters);
    }

    private string TranslateExpression(FilterExpression filter, LayerDefinition layer)
    {
        try
        {
            if (++_depth > MaxExpressionDepth)
            {
                throw new ArgumentException(
                    $"Filter expression exceeds the maximum nesting depth of {MaxExpressionDepth}.");
            }

            return filter switch
            {
                BinaryExpression bin => TranslateBinary(bin, layer),
                UnaryExpression un => TranslateUnary(un, layer),
                PropertyReference prop => TranslateProperty(prop, layer),
                Literal lit => TranslateLiteral(lit),
                SpatialPredicate spatial => TranslateSpatial(spatial, layer),
                SpatialDistancePredicate spatialDistance => TranslateSpatialDistance(spatialDistance, layer),
                ValueList list => TranslateValueList(list, layer),
                _ => throw new NotSupportedException(
                    $"Filter expression '{filter.GetType().Name}' is not supported by the MySQL/MariaDB provider.")
            };
        }
        finally
        {
            _depth--;
        }
    }

    private string TranslateBinary(BinaryExpression binary, LayerDefinition layer)
    {
        if (binary.Right is ValueList valueList && valueList.Values.Count == 0)
        {
            return binary.Operator switch
            {
                BinaryOperator.In => "FALSE",
                BinaryOperator.NotIn => "TRUE",
                _ => TranslateBinaryWithValues(binary, layer)
            };
        }

        return TranslateBinaryWithValues(binary, layer);
    }

    private string TranslateBinaryWithValues(BinaryExpression binary, LayerDefinition layer)
    {
        var left = TranslateExpression(binary.Left, layer);
        var right = TranslateExpression(binary.Right, layer);

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
                $"Binary operator '{binary.Operator}' is not supported by the MySQL/MariaDB provider.")
        };

        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            return $"({left} {op} {right})";
        }

        if (binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract or
            BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo)
        {
            return $"({left} {op} {right})";
        }

        return $"{left} {op} {right}";
    }

    private string TranslateUnary(UnaryExpression unary, LayerDefinition layer)
    {
        var operand = TranslateExpression(unary.Operand, layer);

        return unary.Operator switch
        {
            UnaryOperator.Not => $"NOT ({operand})",
            UnaryOperator.IsNull => $"{operand} IS NULL",
            UnaryOperator.IsNotNull => $"{operand} IS NOT NULL",
            UnaryOperator.Negate => $"-({operand})",
            _ => throw new NotSupportedException(
                $"Unary operator '{unary.Operator}' is not supported by the MySQL/MariaDB provider.")
        };
    }

    private static string TranslateProperty(PropertyReference property, LayerDefinition layer)
    {
        var field = layer.Fields.FirstOrDefault(f =>
            f.Name.Equals(property.PropertyName, StringComparison.OrdinalIgnoreCase));

        if (field is null && !IsGeometryAlias(property.PropertyName))
        {
            throw new ArgumentException(
                $"Field '{property.PropertyName}' is not defined on layer '{layer.Name}'.");
        }

        var name = field?.Name ?? property.PropertyName;
        return MySqlIdentifier.Quote(name);
    }

    private string TranslateLiteral(Literal literal)
    {
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(literal.Value);
        return paramName;
    }

    private string TranslateSpatial(SpatialPredicate spatial, LayerDefinition layer)
    {
        var left = TranslateGeometryExpression(spatial.Left, layer);
        var right = TranslateGeometryExpression(spatial.Right, layer);

        return spatial.Operator switch
        {
            // Refer to MySqlFeatureQueryBuilder.Spatial for the same predicate→function mapping
            // applied to FeatureQuery.SpatialFilter; both paths use identical MySQL functions.
            SpatialOperator.Intersects => $"MBRIntersects({left}, {right}) AND ST_Intersects({left}, {right})",
            SpatialOperator.Contains => $"ST_Contains({left}, {right})",
            SpatialOperator.Within => $"ST_Within({left}, {right})",
            SpatialOperator.Crosses => $"ST_Crosses({left}, {right})",
            SpatialOperator.Touches => $"ST_Touches({left}, {right})",
            SpatialOperator.Overlaps => $"ST_Overlaps({left}, {right})",
            SpatialOperator.Disjoint => $"ST_Disjoint({left}, {right})",
            SpatialOperator.Equals => $"ST_Equals({left}, {right})",
            _ => throw new NotSupportedException(
                $"Spatial operator '{spatial.Operator}' is not supported by the MySQL/MariaDB provider.")
        };
    }

    private string TranslateSpatialDistance(SpatialDistancePredicate spatial, LayerDefinition layer)
    {
        var left = TranslateGeometryExpression(spatial.Left, layer);
        var right = TranslateGeometryExpression(spatial.Right, layer);
        var distance = TranslateExpression(spatial.Distance, layer);

        return spatial.Operator switch
        {
            // ST_Distance_Sphere is approximate (WGS84 spheroid) and point-only by definition.
            // Documented as a known limitation in the operator docs.
            SpatialOperator.DWithin => $"ST_Distance_Sphere({left}, {right}) <= {distance}",
            SpatialOperator.Beyond => $"ST_Distance_Sphere({left}, {right}) > {distance}",
            _ => throw new NotSupportedException(
                $"Spatial distance operator '{spatial.Operator}' is not supported by the MySQL/MariaDB provider.")
        };
    }

    private string TranslateGeometryExpression(FilterExpression expression, LayerDefinition layer)
    {
        switch (expression)
        {
            case GeometryLiteral geometry:
                {
                    if (geometry.Srid != 0 && geometry.Srid != layer.SpatialReference.Wkid)
                    {
                        throw new NotSupportedException(
                            $"Cross-SRID geometry literals are not supported by the MySQL/MariaDB provider " +
                            $"(layer SRID is {layer.SpatialReference.Wkid}, literal SRID is {geometry.Srid}). " +
                            $"Pre-project geometries to the layer SRID before filtering.");
                    }

                    var wkbParam = $"@p{_paramIndex++}";
                    _parameters.Add(geometry.Wkb);
                    var sridLiteral = layer.SpatialReference.Wkid.ToString(CultureInfo.InvariantCulture);
                    return $"ST_GeomFromWKB({wkbParam}, {sridLiteral})";
                }
            case PropertyReference property:
                {
                    var field = layer.Fields.FirstOrDefault(f =>
                        f.Name.Equals(property.PropertyName, StringComparison.OrdinalIgnoreCase));

                    if (field is null)
                    {
                        if (IsGeometryAlias(property.PropertyName))
                        {
                            return GetGeometryColumnExpression(layer);
                        }

                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    if (!field.IsGeometry)
                    {
                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    return GetGeometryColumnExpression(layer);
                }
            default:
                throw new NotSupportedException(
                    $"Geometry expression '{expression.GetType().Name}' is not supported by the MySQL/MariaDB provider.");
        }
    }

    private string TranslateValueList(ValueList valueList, LayerDefinition layer)
    {
        var values = valueList.Values.Select(v => TranslateExpression(v, layer));
        return $"({string.Join(", ", values)})";
    }

    private static string GetGeometryColumnExpression(LayerDefinition layer)
    {
        var geomField = layer.GeometryField?.Name ?? "geometry";
        return MySqlIdentifier.Quote(geomField);
    }

    private static bool IsGeometryAlias(string propertyName)
        => propertyName.Equals("geom", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("geometry", StringComparison.OrdinalIgnoreCase);
}
