// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore.Services;

namespace Honua.Db.Postgres.Queries.Filters;

internal partial class PostgresSqlFilterTranslator
{
    private enum DistanceOperandOrder
    {
        PropertyLeft,
        PropertyRight
    }

    private sealed class DistanceOperands
    {
        public required PropertyReference Property { get; init; }
        public required GeometryLiteral Geometry { get; init; }
        public required DistanceOperandOrder Order { get; init; }
        public required double DistanceMeters { get; init; }
    }

    private string? TranslateGeographicDWithinWithPrefilter(
        SpatialDistancePredicate spatial,
        FilterTranslationContext context)
    {
        var operands = ExtractDistanceOperands(spatial);
        if (operands is null)
        {
            return null;
        }

        ValidateGeometryProperty(operands.Property, context);
        var wkbParam = AddParameter(operands.Geometry.Wkb);
        var sridParam = AddParameter(operands.Geometry.Srid);
        var distanceSql = TranslateExpression(spatial.Distance, context);

        var columnGeom = GetGeometryColumnExpression(context);
        var wgs84Srid = SpatialReference.WGS84.Wkid;
        var columnGeo = context.Wkid == wgs84Srid
            ? $"{columnGeom}::geography"
            : $"ST_Transform({columnGeom}, {wgs84Srid})::geography";
        var baseLiteralGeom = $"ST_GeomFromWKB({wkbParam}, {sridParam})";
        var literalGeo = operands.Geometry.Srid == wgs84Srid
            ? $"{baseLiteralGeom}::geography"
            : $"ST_Transform({baseLiteralGeom}, {wgs84Srid})::geography";
        var storageFilterGeom = operands.Geometry.Srid == context.Wkid
            ? baseLiteralGeom
            : DatumTransformSql.BuildTransformExpression(baseLiteralGeom, context.Wkid, selection: null, fromSrid: operands.Geometry.Srid);

        // Pair the exact geodesic ST_DWithin with a storage-CRS && envelope pre-filter
        // (matching FeatureQueryBuilder.Spatial) so the GiST index prunes candidates first.
        var prefilter = FeatureQueryBuilder.BuildDistanceEnvelopePrefilter(
            columnGeom, storageFilterGeom, literalGeo, operands.DistanceMeters, context.Wkid);
        var left = operands.Order == DistanceOperandOrder.PropertyLeft ? columnGeo : literalGeo;
        var right = operands.Order == DistanceOperandOrder.PropertyLeft ? literalGeo : columnGeo;
        return $"({prefilter} AND ST_DWithin({left}, {right}, {distanceSql}))";
    }

    private static DistanceOperands? ExtractDistanceOperands(SpatialDistancePredicate spatial)
    {
        if (spatial.Distance is not Literal { Type: LiteralType.Number, Value: not null } distLiteral)
        {
            return null;
        }

        var distanceMeters = Convert.ToDouble(distLiteral.Value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(distanceMeters) || distanceMeters < 0)
        {
            return null;
        }

        if (spatial.Left is PropertyReference leftProp && spatial.Right is GeometryLiteral rightGeom)
        {
            return new DistanceOperands
            {
                Property = leftProp,
                Geometry = rightGeom,
                Order = DistanceOperandOrder.PropertyLeft,
                DistanceMeters = distanceMeters
            };
        }

        if (spatial.Right is PropertyReference rightProp && spatial.Left is GeometryLiteral leftGeom)
        {
            return new DistanceOperands
            {
                Property = rightProp,
                Geometry = leftGeom,
                Order = DistanceOperandOrder.PropertyRight,
                DistanceMeters = distanceMeters
            };
        }

        return null;
    }

    private void ValidateGeometryProperty(PropertyReference property, FilterTranslationContext context)
    {
        var field = context.TryGetField(property.PropertyName);
        if (field is null && !IsGeometryAlias(property.PropertyName))
        {
            throw UnknownFilterFieldException.ForProperty(property.PropertyName);
        }

        if (field is not null && !field.Value.IsGeometry)
        {
            throw new ArgumentException($"Field '{property.PropertyName}' is not a geometry field");
        }
    }
}
