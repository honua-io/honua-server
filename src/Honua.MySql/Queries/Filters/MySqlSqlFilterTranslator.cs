// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
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

    private readonly MySqlEngineFlavor _engineFlavor;

    private int _paramIndex;
    private int _depth;
    private readonly List<object?> _parameters = [];

    public MySqlSqlFilterTranslator(MySqlEngineFlavor engineFlavor = MySqlEngineFlavor.Mysql)
    {
        _engineFlavor = engineFlavor;
    }

    /// <inheritdoc />
    public SqlFragment Translate(FilterExpression filter, LayerDefinition layer)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(layer);
        return TranslateCore(filter, FilterTranslationContext.FromLayer(layer));
    }

    /// <inheritdoc />
    public SqlFragment Translate(FilterExpression filter, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(resource);
        return TranslateCore(filter, FilterTranslationContext.FromResource(resource));
    }

    private SqlFragment TranslateCore(FilterExpression filter, FilterTranslationContext context)
    {
        _paramIndex = 0;
        _depth = 0;
        _parameters.Clear();

        var sql = TranslateExpression(filter, context);
        return new SqlFragment(sql, _parameters);
    }

    private string TranslateExpression(FilterExpression filter, FilterTranslationContext context)
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
                BinaryExpression bin => TranslateBinary(bin, context),
                UnaryExpression un => TranslateUnary(un, context),
                PropertyReference prop => TranslateProperty(prop, context),
                Literal lit => TranslateLiteral(lit),
                SpatialPredicate spatial => TranslateSpatial(spatial, context),
                SpatialDistancePredicate spatialDistance => TranslateSpatialDistance(spatialDistance, context),
                ValueList list => TranslateValueList(list, context),
                _ => throw new NotSupportedException(
                    $"Filter expression '{filter.GetType().Name}' is not supported by the MySQL/MariaDB provider.")
            };
        }
        finally
        {
            _depth--;
        }
    }

    private string TranslateBinary(BinaryExpression binary, FilterTranslationContext context)
    {
        if (binary.Right is ValueList valueList && valueList.Values.Count == 0)
        {
            return binary.Operator switch
            {
                BinaryOperator.In => "FALSE",
                BinaryOperator.NotIn => "TRUE",
                _ => TranslateBinaryWithValues(binary, context)
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

    private string TranslateUnary(UnaryExpression unary, FilterTranslationContext context)
    {
        var operand = TranslateExpression(unary.Operand, context);

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

    private static string TranslateProperty(PropertyReference property, FilterTranslationContext context)
    {
        var field = context.TryGetField(property.PropertyName);

        if (field is not null)
        {
            return MySqlIdentifier.Quote(field.Value.Name);
        }

        // No matching field. Geometry aliases (geom/shape/geometry) resolve to the
        // resource's configured geometry column so predicates such as `geom IS NOT NULL`
        // keep working when the backing column has a non-default name. This mirrors the
        // alias handling in TranslateGeometryExpression so attribute and spatial paths agree.
        if (IsGeometryAlias(property.PropertyName))
        {
            return GetGeometryColumnExpression(context);
        }

        throw new ArgumentException(
            $"Field '{property.PropertyName}' is not defined on layer '{context.ResourceName}'.");
    }

    private string TranslateLiteral(Literal literal)
    {
        var paramName = $"@p{_paramIndex++}";
        _parameters.Add(literal.Value);
        return paramName;
    }

    private string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context)
    {
        var left = TranslateGeometryExpression(spatial.Left, context);
        var right = TranslateGeometryExpression(spatial.Right, context);

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

    private string TranslateSpatialDistance(SpatialDistancePredicate spatial, FilterTranslationContext context)
    {
        // ST_Distance_Sphere expects WGS84 point geometries; on polygons or lines it would
        // silently degrade to centroid math. The MySqlFeatureQueryBuilder distance path
        // applies the same point-only guard against FeatureQuery.SpatialFilter — the
        // CQL2 translator must enforce the same contract so OGC API Features / OData
        // distance predicates against non-point layers fail loudly instead of returning
        // misleading results.
        if (context.GeometryType is not GeometryType.Point and not GeometryType.MultiPoint)
        {
            throw new NotSupportedException(
                $"Distance spatial filters are only supported for point layers in the MySQL/MariaDB provider " +
                $"(layer geometry is {context.GeometryType}). ST_Distance_Sphere expects point geometries.");
        }

        // Mirror the MySqlFeatureQueryBuilder.Spatial SRID guard: ST_Distance_Sphere is
        // documented for EPSG:4326 / SRID 0 only. Projected SRSes raise
        // ER_INVALID_GIS_DATA at the engine; other geographic SRSes use a different
        // mean radius. Reject up front so the CQL2 / OGC API Features filter path fails
        // with the same descriptive contract as the FeatureQuery.SpatialFilter path.
        if (!MySqlSpatialSql.IsDistanceSphereCompatibleSrid(context.Wkid))
        {
            throw new NotSupportedException(
                $"Distance spatial filters require the layer SRID to be EPSG:4326 or SRID 0 in the MySQL/MariaDB provider " +
                $"(layer SRID is {context.Wkid}). ST_Distance_Sphere is documented as a WGS84 spherical approximation; " +
                $"pre-project the layer to EPSG:4326 before issuing distance filters.");
        }

        var left = TranslateGeometryExpression(spatial.Left, context);
        var right = TranslateGeometryExpression(spatial.Right, context);
        var distance = TranslateExpression(spatial.Distance, context);

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

    private string TranslateGeometryExpression(FilterExpression expression, FilterTranslationContext context)
    {
        switch (expression)
        {
            case GeometryLiteral geometry:
                {
                    if (geometry.Srid != 0 && geometry.Srid != context.Wkid)
                    {
                        throw new NotSupportedException(
                            $"Cross-SRID geometry literals are not supported by the MySQL/MariaDB provider " +
                            $"(layer SRID is {context.Wkid}, literal SRID is {geometry.Srid}). " +
                            $"Pre-project geometries to the layer SRID before filtering.");
                    }

                    var wkbParam = $"@p{_paramIndex++}";
                    _parameters.Add(geometry.Wkb);
                    return MySqlSpatialSql.GeomFromWkb(wkbParam, context.Wkid, _engineFlavor);
                }
            case PropertyReference property:
                {
                    var field = context.TryGetField(property.PropertyName);

                    if (field is null)
                    {
                        if (IsGeometryAlias(property.PropertyName))
                        {
                            return GetGeometryColumnExpression(context);
                        }

                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    if (!field.Value.IsGeometry)
                    {
                        throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
                    }

                    return GetGeometryColumnExpression(context);
                }
            default:
                throw new NotSupportedException(
                    $"Geometry expression '{expression.GetType().Name}' is not supported by the MySQL/MariaDB provider.");
        }
    }

    private string TranslateValueList(ValueList valueList, FilterTranslationContext context)
    {
        var values = valueList.Values.Select(v => TranslateExpression(v, context));
        return $"({string.Join(", ", values)})";
    }

    private static string GetGeometryColumnExpression(FilterTranslationContext context)
    {
        var geomField = context.GeometryColumnName ?? "geometry";
        return MySqlIdentifier.Quote(geomField);
    }

    private static bool IsGeometryAlias(string propertyName)
        => propertyName.Equals("geom", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("geometry", StringComparison.OrdinalIgnoreCase);
}
