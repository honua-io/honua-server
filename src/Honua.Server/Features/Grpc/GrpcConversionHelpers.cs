// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Google.Protobuf;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using DomainDistanceUnit = Honua.Core.Features.FeatureStore.Domain.DistanceUnit;
using DomainGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;
using DomainSpatialFilter = Honua.Core.Features.FeatureStore.Domain.SpatialFilter;
using DomainSpatialRelationship = Honua.Core.Features.FeatureStore.Domain.SpatialRelationship;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// Converts between domain types and proto-generated message types.
/// </summary>
internal static class GrpcConversionHelpers
{
    private static readonly GeometryFactory GeoFactory = new();

    [ThreadStatic]
    private static WKBReader? _wkbReader;
    [ThreadStatic]
    private static WKBWriter? _wkbWriter;

    private static WKBReader WkbReader => _wkbReader ??= new WKBReader();
    private static WKBWriter WkbWriter => _wkbWriter ??= new WKBWriter();

    /// <summary>
    /// Converts a proto QueryFeaturesRequest into a domain FeatureQuery.
    /// </summary>
    public static FeatureQuery ToFeatureQuery(Proto.QueryFeaturesRequest request)
    {
        var query = new FeatureQuery
        {
            Where = string.IsNullOrEmpty(request.Where) ? null : request.Where,
            ObjectIds = request.ObjectIds.Count > 0
                ? request.ObjectIds.ToImmutableArray()
                : null,
            OutFields = request.OutFields.Count > 0
                ? request.OutFields.ToImmutableArray()
                : null,
            Offset = request.ResultOffset > 0 ? request.ResultOffset : null,
            Limit = request.ResultRecordCount > 0 ? request.ResultRecordCount : null,
            OutputSrid = TryResolveOutputSrid(request.OutSr),
            Distinct = request.ReturnDistinct,
            SpatialFilter = request.SpatialFilter != null
                ? ToSpatialFilter(request.SpatialFilter)
                : null,
        };

        if (!string.IsNullOrEmpty(request.OrderBy))
        {
            query = query with { OrderBy = ParseOrderBy(request.OrderBy) };
        }

        if (request.OutStatistics.Count > 0)
        {
            query = query with
            {
                OutStatistics = request.OutStatistics
                    .Select(ToStatisticDefinition)
                    .ToImmutableArray()
            };
        }

        if (request.GroupBy.Count > 0)
        {
            query = query with { GroupByFields = request.GroupBy.ToImmutableArray() };
        }

        return query;
    }

    /// <summary>
    /// Resolves an output SRID from proto spatial reference parameters.
    /// </summary>
    public static int? TryResolveOutputSrid(Proto.SpatialReference? spatialReference)
    {
        if (spatialReference == null)
        {
            return null;
        }

        if (spatialReference.Wkid > 0)
        {
            return spatialReference.Wkid;
        }

        if (spatialReference.LatestWkid > 0)
        {
            return spatialReference.LatestWkid;
        }

        return null;
    }

    /// <summary>
    /// Builds effective geometry limits for a gRPC request.
    /// </summary>
    public static GeometryLimits CreateEffectiveGeometryLimits(GeometryLimits baseLimits, Proto.QueryFeaturesRequest request)
    {
        var precisionOverride = request.GeometryPrecision > 0 ? (int?)request.GeometryPrecision : null;
        var simplifyToleranceOverride = request.MaxAllowableOffset > 0 ? (double?)request.MaxAllowableOffset : null;

        return GeometryOutputProcessor.CreateEffectiveLimits(
            baseLimits,
            precisionOverride,
            simplifyToleranceOverride,
            forceSimplify: simplifyToleranceOverride.HasValue);
    }

    /// <summary>
    /// Converts a domain Feature to a proto Feature message.
    /// </summary>
    public static Proto.Feature ToProtoFeature(
        Feature feature,
        bool includeGeometry = true,
        GeometryLimits? geometryLimits = null)
    {
        var proto = new Proto.Feature { Id = feature.Id };

        foreach (var (key, value) in feature.Attributes)
        {
            proto.Attributes[key] = ToAttributeValue(value);
        }

        if (includeGeometry && feature.Geometry is { Length: > 0 })
        {
            proto.Geometry = ToProtoGeometry(feature.Geometry, geometryLimits);
        }

        return proto;
    }

    /// <summary>
    /// Converts a domain LayerDefinition's fields to proto FieldDefinition messages.
    /// </summary>
    public static Proto.FieldDefinition ToProtoField(FieldDefinition field)
    {
        return new Proto.FieldDefinition
        {
            Name = field.Name,
            FieldType = ToProtoFieldType(field.Type),
            Length = field.Length ?? 0,
            Nullable = field.Nullable
        };
    }

    /// <summary>
    /// Converts a domain SpatialReference to a proto SpatialReference.
    /// </summary>
    public static Proto.SpatialReference ToProtoSpatialReference(SpatialReference sr)
    {
        return new Proto.SpatialReference
        {
            Wkid = sr.Wkid,
            LatestWkid = sr.LatestWkid ?? 0,
            Wkt = sr.Wkt ?? string.Empty
        };
    }

    /// <summary>
    /// Converts a domain GeometryType to a proto GeometryType.
    /// </summary>
    public static Proto.GeometryType ToProtoGeometryType(DomainGeometryType geometryType)
    {
        return geometryType switch
        {
            DomainGeometryType.Point => Proto.GeometryType.Point,
            DomainGeometryType.MultiPoint => Proto.GeometryType.MultiPoint,
            DomainGeometryType.LineString => Proto.GeometryType.LineString,
            DomainGeometryType.MultiLineString => Proto.GeometryType.MultiLineString,
            DomainGeometryType.Polygon => Proto.GeometryType.Polygon,
            DomainGeometryType.MultiPolygon => Proto.GeometryType.MultiPolygon,
            DomainGeometryType.GeometryCollection => Proto.GeometryType.GeometryCollection,
            DomainGeometryType.None => Proto.GeometryType.None,
            _ => Proto.GeometryType.Unspecified
        };
    }

    /// <summary>
    /// Converts a domain FeatureExtent to a proto Extent.
    /// </summary>
    public static Proto.Extent ToProtoExtent(FeatureExtent extent, SpatialReference? sr)
    {
        var proto = new Proto.Extent
        {
            Xmin = extent.MinX,
            Ymin = extent.MinY,
            Xmax = extent.MaxX,
            Ymax = extent.MaxY
        };

        if (sr.HasValue)
        {
            proto.SpatialReference = ToProtoSpatialReference(sr.Value);
        }

        return proto;
    }

    private static Proto.AttributeValue ToAttributeValue(object? value)
    {
        var av = new Proto.AttributeValue();
        switch (value)
        {
            case null:
                av.NullValue = Proto.NullValue.NullValue;
                break;
            case string s:
                av.StringValue = s;
                break;
            case int i:
                av.Int32Value = i;
                break;
            case long l:
                av.Int64Value = l;
                break;
            case double d:
                av.DoubleValue = d;
                break;
            case float f:
                av.FloatValue = f;
                break;
            case bool b:
                av.BoolValue = b;
                break;
            case DateTime dt:
                av.DatetimeValue = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                break;
            case DateTimeOffset dto:
                av.DatetimeValue = dto.ToUnixTimeMilliseconds();
                break;
            case byte[] bytes:
                av.BytesValue = ByteString.CopyFrom(bytes);
                break;
            case short s:
                av.Int32Value = s;
                break;
            case decimal dec:
                av.DoubleValue = (double)dec;
                break;
            default:
                av.StringValue = value.ToString() ?? string.Empty;
                break;
        }
        return av;
    }

    private static Proto.Geometry ToProtoGeometry(byte[] wkb, GeometryLimits? geometryLimits)
    {
        var geometry = WkbReader.Read(wkb);
        if (geometryLimits != null)
        {
            geometry = GeometryOutputProcessor.ApplyLimits(geometry, geometryLimits) ?? geometry;
        }

        var proto = new Proto.Geometry();

        switch (geometry)
        {
            case Point point:
                proto.Point = new Proto.PointGeometry { X = point.X, Y = point.Y };
                if (!double.IsNaN(point.Z))
                    proto.Point.Z = point.Z;
                if (!double.IsNaN(point.M))
                    proto.Point.M = point.M;
                break;

            case MultiPoint multiPoint:
                var mp = new Proto.MultiPointGeometry();
                foreach (var p in multiPoint.Geometries.Cast<Point>())
                {
                    var pp = new Proto.PointGeometry { X = p.X, Y = p.Y };
                    if (!double.IsNaN(p.Z))
                        pp.Z = p.Z;
                    if (!double.IsNaN(p.M))
                        pp.M = p.M;
                    mp.Points.Add(pp);
                }
                proto.MultiPoint = mp;
                break;

            case LineString lineString:
                var polyline = new Proto.PolylineGeometry();
                polyline.Paths.Add(ToCoordinateSequence(lineString.CoordinateSequence));
                proto.Polyline = polyline;
                break;

            case MultiLineString multiLineString:
                var mlPoly = new Proto.PolylineGeometry();
                foreach (var ls in multiLineString.Geometries.Cast<LineString>())
                {
                    mlPoly.Paths.Add(ToCoordinateSequence(ls.CoordinateSequence));
                }
                proto.Polyline = mlPoly;
                break;

            case Polygon polygon:
                var poly = new Proto.PolygonGeometry();
                poly.Rings.Add(ToCoordinateSequence(polygon.ExteriorRing.CoordinateSequence));
                foreach (var hole in polygon.InteriorRings)
                {
                    poly.Rings.Add(ToCoordinateSequence(hole.CoordinateSequence));
                }
                proto.Polygon = poly;
                break;

            case MultiPolygon multiPolygon:
                var mpoly = new Proto.MultiPolygonGeometry();
                foreach (var pg in multiPolygon.Geometries.Cast<Polygon>())
                {
                    var polyPart = new Proto.PolygonGeometry();
                    polyPart.Rings.Add(ToCoordinateSequence(pg.ExteriorRing.CoordinateSequence));
                    foreach (var hole in pg.InteriorRings)
                    {
                        polyPart.Rings.Add(ToCoordinateSequence(hole.CoordinateSequence));
                    }
                    mpoly.Polygons.Add(polyPart);
                }
                proto.MultiPolygon = mpoly;
                break;

            case GeometryCollection:
                throw new InvalidOperationException(
                    "GeometryCollection is not representable in the gRPC proto schema. " +
                    "Convert to a specific geometry type before serialization.");
        }

        return proto;
    }

    private static Proto.CoordinateSequence ToCoordinateSequence(
        NetTopologySuite.Geometries.CoordinateSequence coords)
    {
        var seq = new Proto.CoordinateSequence { Coords = { Capacity = coords.Count } };
        for (var i = 0; i < coords.Count; i++)
        {
            var c = new Proto.Coordinate
            {
                X = coords.GetX(i),
                Y = coords.GetY(i)
            };
            if (coords.HasZ)
            {
                var z = coords.GetZ(i);
                if (!double.IsNaN(z))
                    c.Z = z;
            }
            if (coords.HasM)
            {
                var m = coords.GetM(i);
                if (!double.IsNaN(m))
                    c.M = m;
            }
            seq.Coords.Add(c);
        }
        return seq;
    }

    private static Proto.FieldType ToProtoFieldType(FieldType fieldType)
    {
        return fieldType switch
        {
            FieldType.String => Proto.FieldType.String,
            FieldType.Integer => Proto.FieldType.Integer,
            FieldType.BigInteger => Proto.FieldType.BigInteger,
            FieldType.Double => Proto.FieldType.Double,
            FieldType.Float => Proto.FieldType.Float,
            FieldType.Boolean => Proto.FieldType.Boolean,
            FieldType.DateTime => Proto.FieldType.DateTime,
            FieldType.Date => Proto.FieldType.Date,
            FieldType.Time => Proto.FieldType.Time,
            FieldType.Geometry => Proto.FieldType.Geometry,
            FieldType.Json => Proto.FieldType.Json,
            FieldType.Binary => Proto.FieldType.Binary,
            FieldType.Uuid => Proto.FieldType.Uuid,
            _ => Proto.FieldType.Unspecified
        };
    }

    private static DomainSpatialFilter? ToSpatialFilter(Proto.SpatialFilter filter)
    {
        if (filter.Geometry == null)
            return null;

        var geometryWkb = ProtoGeometryToWkb(filter.Geometry);
        if (geometryWkb == null)
            return null;

        return new DomainSpatialFilter
        {
            Geometry = geometryWkb,
            Srid = filter.SpatialReference?.Wkid > 0 ? filter.SpatialReference.Wkid : null,
            SpatialRelationship = ToDomainSpatialRelationship(filter.SpatialRelationship),
            Distance = filter.Distance > 0 ? filter.Distance : null,
            DistanceUnit = ToDomainDistanceUnit(filter.DistanceUnit),
            NearestCount = filter.NearestCount > 0 ? filter.NearestCount : null,
            ReturnDistance = filter.ReturnDistance
        };
    }

    private static byte[]? ProtoGeometryToWkb(Proto.Geometry geometry)
    {
        Geometry? ntsGeometry = geometry.ShapeCase switch
        {
            Proto.Geometry.ShapeOneofCase.Point => new Point(geometry.Point.X, geometry.Point.Y),
            Proto.Geometry.ShapeOneofCase.Polygon => BuildPolygon(geometry.Polygon),
            Proto.Geometry.ShapeOneofCase.Polyline => BuildPolyline(geometry.Polyline),
            Proto.Geometry.ShapeOneofCase.MultiPoint => BuildMultiPoint(geometry.MultiPoint),
            Proto.Geometry.ShapeOneofCase.MultiPolygon => BuildMultiPolygon(geometry.MultiPolygon),
            _ => null
        };

        if (ntsGeometry == null)
            return null;

        return WkbWriter.Write(ntsGeometry);
    }

    private static Polygon BuildPolygon(Proto.PolygonGeometry poly)
    {
        if (poly.Rings.Count == 0)
            return Polygon.Empty;

        var shell = GeoFactory.CreateLinearRing(
            poly.Rings[0].Coords.Select(c => new Coordinate(c.X, c.Y)).ToArray());
        var holes = poly.Rings.Skip(1)
            .Select(r => GeoFactory.CreateLinearRing(
                r.Coords.Select(c => new Coordinate(c.X, c.Y)).ToArray()))
            .ToArray();

        return GeoFactory.CreatePolygon(shell, holes);
    }

    private static MultiLineString BuildPolyline(Proto.PolylineGeometry polyline)
    {
        var lines = polyline.Paths.Select(p =>
            GeoFactory.CreateLineString(p.Coords.Select(c => new Coordinate(c.X, c.Y)).ToArray()))
            .ToArray();
        return GeoFactory.CreateMultiLineString(lines);
    }

    private static MultiPoint BuildMultiPoint(Proto.MultiPointGeometry mp)
    {
        var points = mp.Points.Select(p => GeoFactory.CreatePoint(new Coordinate(p.X, p.Y))).ToArray();
        return GeoFactory.CreateMultiPoint(points);
    }

    private static MultiPolygon BuildMultiPolygon(Proto.MultiPolygonGeometry multiPoly)
    {
        var polygons = multiPoly.Polygons.Select(BuildPolygon).ToArray();
        return GeoFactory.CreateMultiPolygon(polygons);
    }

    private static DomainSpatialRelationship ToDomainSpatialRelationship(
        Proto.SpatialRelationship rel)
    {
        return rel switch
        {
            Proto.SpatialRelationship.Intersects => DomainSpatialRelationship.Intersects,
            Proto.SpatialRelationship.Within => DomainSpatialRelationship.Within,
            Proto.SpatialRelationship.Contains => DomainSpatialRelationship.Contains,
            Proto.SpatialRelationship.EnvelopeIntersects => DomainSpatialRelationship.EnvelopeIntersects,
            Proto.SpatialRelationship.Crosses => DomainSpatialRelationship.Crosses,
            Proto.SpatialRelationship.Touches => DomainSpatialRelationship.Touches,
            Proto.SpatialRelationship.Overlaps => DomainSpatialRelationship.Overlaps,
            Proto.SpatialRelationship.Disjoint => DomainSpatialRelationship.Disjoint,
            Proto.SpatialRelationship.Equals => DomainSpatialRelationship.Equals,
            Proto.SpatialRelationship.WithinDistance => DomainSpatialRelationship.WithinDistance,
            Proto.SpatialRelationship.BeyondDistance => DomainSpatialRelationship.BeyondDistance,
            Proto.SpatialRelationship.NearestNeighbor => DomainSpatialRelationship.NearestNeighbor,
            _ => DomainSpatialRelationship.Intersects
        };
    }

    private static DomainDistanceUnit ToDomainDistanceUnit(Proto.DistanceUnit unit)
    {
        return unit switch
        {
            Proto.DistanceUnit.Meters => DomainDistanceUnit.Meters,
            Proto.DistanceUnit.Feet => DomainDistanceUnit.Feet,
            Proto.DistanceUnit.Kilometers => DomainDistanceUnit.Kilometers,
            Proto.DistanceUnit.Miles => DomainDistanceUnit.Miles,
            _ => DomainDistanceUnit.Meters
        };
    }

    private static StatisticDefinition ToStatisticDefinition(Proto.StatisticDefinition proto)
    {
        return new StatisticDefinition
        {
            OnStatisticField = proto.OnStatisticField,
            StatisticType = proto.StatisticType switch
            {
                Proto.StatisticType.Count => StatisticType.Count,
                Proto.StatisticType.Sum => StatisticType.Sum,
                Proto.StatisticType.Min => StatisticType.Min,
                Proto.StatisticType.Max => StatisticType.Max,
                Proto.StatisticType.Avg => StatisticType.Avg,
                Proto.StatisticType.Stddev => StatisticType.Stddev,
                Proto.StatisticType.Var => StatisticType.Var,
                _ => StatisticType.Count
            },
            OutStatisticFieldName = proto.OutStatisticFieldName
        };
    }

    private static ImmutableArray<OrderByClause> ParseOrderBy(string orderBy)
    {
        var clauses = orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = ImmutableArray.CreateBuilder<OrderByClause>(clauses.Length);

        foreach (var clause in clauses)
        {
            var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var field = parts[0];
            var ascending = parts.Length < 2 ||
                !parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);

            builder.Add(new OrderByClause(field, ascending));
        }

        return builder.ToImmutable();
    }

}
