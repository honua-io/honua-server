// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;

namespace Honua.DuckDB.Queries.Filters;

/// <summary>
/// Translates the shared filter AST into parameterized DuckDB SQL.
/// </summary>
internal sealed class DuckDbSqlFilterTranslator : SqlFilterExpressionVisitorBase, ISqlFilterTranslator
{
    public DuckDbSqlFilterTranslator()
        : base(DuckDbSqlDialect.Instance)
    {
    }

    /// <inheritdoc />
    public SqlFragment Translate(FilterExpression filter, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(resource);
        return Translate(filter, FilterTranslationContext.FromResource(resource));
    }

    protected override string TranslateBinary(BinaryExpression binary, FilterTranslationContext context)
    {
        if (binary.Right is ValueList { Values.Count: 0 })
        {
            return binary.Operator switch
            {
                BinaryOperator.In => "FALSE",
                BinaryOperator.NotIn => "TRUE",
                _ => TranslateBinaryOperands(binary, context)
            };
        }

        return TranslateBinaryOperands(binary, context);
    }

    private string TranslateBinaryOperands(BinaryExpression binary, FilterTranslationContext context)
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
            BinaryOperator.Div => "//",
            BinaryOperator.Power => "^",
            _ => throw Unsupported($"Binary operator '{binary.Operator}'")
        };

        return binary.Operator is BinaryOperator.And or BinaryOperator.Or or
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or
            BinaryOperator.Divide or BinaryOperator.Modulo or BinaryOperator.Div or BinaryOperator.Power
            ? $"({left} {op} {right})"
            : $"{left} {op} {right}";
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
            _ => throw Unsupported($"Unary operator '{unary.Operator}'")
        };
    }

    protected override string TranslateProperty(PropertyReference property, FilterTranslationContext context)
    {
        var field = context.TryGetField(property.PropertyName);
        if (field is not null)
        {
            return Dialect.QuoteIdentifier(field.Value.Name);
        }

        if (IsGeometryAlias(property.PropertyName))
        {
            return GetGeometryColumn(context);
        }

        throw new ArgumentException(
            $"Field '{property.PropertyName}' is not defined on layer '{context.ResourceName}'.");
    }

    protected override string TranslateSpatial(SpatialPredicate spatial, FilterTranslationContext context)
    {
        // Protocol-scoped geodesic routing (OData geo.intersects over Edm.Geography) asks for
        // ellipsoidal intersects semantics so long edges and antimeridian-crossing geometries
        // resolve correctly. DuckDB's spatial extension has no geography/ellipsoidal intersects
        // equivalent to PostGIS's geography cast, so reject rather than silently downgrade to
        // planar-in-degree ST_Intersects (see Honua.Postgres.Queries.Filters.
        // PostgresSqlFilterTranslator.TranslateSpatial for the geography-backed implementation
        // this provider does not have).
        if (spatial.Geodesic)
        {
            throw Unsupported(
                $"Geodesic spatial operator '{spatial.Operator}'; DuckDB has no geography-backed " +
                "ellipsoidal intersects, so this would silently evaluate in planar degree space");
        }

        var left = TranslateGeometryExpression(spatial.Left, context);
        var right = TranslateGeometryExpression(spatial.Right, context);

        // CQL2 defines these operators in AST operand order. In particular, do not apply the
        // FeatureQuery.SpatialFilter Within/Contains inversion: that path models a feature
        // geometry plus a separate filter geometry, while CQL2 permits either operand order.
        return spatial.Operator switch
        {
            SpatialOperator.Intersects => $"ST_Intersects({left}, {right})",
            SpatialOperator.Contains => $"ST_Contains({left}, {right})",
            SpatialOperator.Within => $"ST_Within({left}, {right})",
            SpatialOperator.Crosses => $"ST_Crosses({left}, {right})",
            SpatialOperator.Touches => $"ST_Touches({left}, {right})",
            SpatialOperator.Overlaps => $"ST_Overlaps({left}, {right})",
            SpatialOperator.Disjoint => $"ST_Disjoint({left}, {right})",
            SpatialOperator.Equals => $"ST_Equals({left}, {right})",
            _ => throw Unsupported($"Spatial operator '{spatial.Operator}'")
        };
    }

    protected override string TranslateSpatialDistance(
        SpatialDistancePredicate spatial,
        FilterTranslationContext context)
    {
        var left = TranslateGeometryExpression(spatial.Left, context);
        var right = TranslateGeometryExpression(spatial.Right, context);
        var distance = TranslateExpression(spatial.Distance, context);

        if (DistanceConversions.IsGeographicSrid(context.Wkid))
        {
            if (context.GeometryType is not GeometryType.Point)
            {
                throw Unsupported(
                    $"Geographic distance spatial filters for {context.GeometryType} layers; " +
                    "DuckDB ST_Distance_Spheroid accepts point geometries only");
            }

            EnsurePointGeometryLiteral(spatial.Left);
            EnsurePointGeometryLiteral(spatial.Right);

            // DuckDB's spheroid function expects latitude/longitude, while Honua's axis
            // contract is always X/Y (longitude/latitude).
            var geodesicDistance =
                $"ST_Distance_Spheroid(ST_FlipCoordinates({left}), ST_FlipCoordinates({right}))";
            return spatial.Operator switch
            {
                SpatialOperator.DWithin => $"{geodesicDistance} <= {distance}",
                SpatialOperator.Beyond => $"{geodesicDistance} > {distance}",
                _ => throw Unsupported($"Spatial distance operator '{spatial.Operator}'")
            };
        }

        if (context.Wkid is >= 4000 and <= 4999)
        {
            throw Unsupported(
                $"Distance spatial filters for SRID {context.Wkid}; the CRS is in the EPSG geographic " +
                "range but is not in the geodesic allowlist, so metre semantics cannot be established");
        }

        return spatial.Operator switch
        {
            SpatialOperator.DWithin => $"ST_DWithin({left}, {right}, {distance})",
            SpatialOperator.Beyond => $"NOT ST_DWithin({left}, {right}, {distance})",
            _ => throw Unsupported($"Spatial distance operator '{spatial.Operator}'")
        };
    }

    private string TranslateGeometryExpression(FilterExpression expression, FilterTranslationContext context)
    {
        switch (expression)
        {
            case GeometryLiteral geometry:
                if (geometry.Srid != 0 && geometry.Srid != context.Wkid)
                {
                    throw Unsupported(
                        $"Cross-SRID geometry literals (layer SRID {context.Wkid}, literal SRID {geometry.Srid}); " +
                        "pre-transform the literal to the layer CRS using an always_xy axis contract");
                }

                return $"ST_GeomFromWKB({AddParameter(geometry.Wkb)})";

            case PropertyReference property:
                var field = context.TryGetField(property.PropertyName);
                if (field is null)
                {
                    if (IsGeometryAlias(property.PropertyName))
                    {
                        return GetGeometryColumn(context);
                    }

                    throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field.");
                }

                if (!field.Value.IsGeometry)
                {
                    throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field.");
                }

                return Dialect.QuoteIdentifier(field.Value.Name);

            default:
                throw Unsupported($"Geometry expression '{expression.GetType().Name}'");
        }
    }

    private static void EnsurePointGeometryLiteral(FilterExpression expression)
    {
        if (expression is not GeometryLiteral geometry)
        {
            return;
        }

        var wkb = geometry.Wkb;
        if (wkb.Length < 5 || wkb[0] is not (0 or 1))
        {
            throw Unsupported(
                "Geographic distance geometry literal with an invalid WKB header; " +
                "DuckDB ST_Distance_Spheroid accepts point geometries only");
        }

        var encodedType = wkb[0] == 1
            ? BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(wkb.AsSpan(1, 4));
        var baseType = (encodedType & 0x1FFFFFFF) % 1000;
        if (baseType != 1)
        {
            throw Unsupported(
                "Geographic distance geometry literal that is not a point; " +
                "DuckDB ST_Distance_Spheroid accepts point geometries only");
        }
    }

    private string GetGeometryColumn(FilterTranslationContext context)
        => Dialect.QuoteIdentifier(context.GeometryColumnName ?? "geometry");

    private static bool IsGeometryAlias(string propertyName)
        => propertyName.Equals("geom", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("shape", StringComparison.OrdinalIgnoreCase) ||
           propertyName.Equals("geometry", StringComparison.OrdinalIgnoreCase);

    private static NotSupportedException Unsupported(string feature)
        => new($"{feature} is not supported by the DuckDB provider.");
}
