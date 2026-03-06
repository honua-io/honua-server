// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using System.Linq;
using Honua.Shared.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Geospatial.V1;

namespace Honua.Shared.Converters;

/// <summary>
/// Conversion helpers between domain types and gRPC proto messages.
/// Uses protocol definitions from geospatial-grpc standard.
/// Works on both server and mobile clients to ensure consistent behavior.
/// </summary>
public static class GrpcConversionHelpers
{
    private static readonly GeometryFactory _geoFactory = new();

    [ThreadStatic]
    private static WKBReader? _wkbReader;
    [ThreadStatic]
    private static WKBWriter? _wkbWriter;

    private static WKBReader WkbReader => _wkbReader ??= new WKBReader();
    private static WKBWriter WkbWriter => _wkbWriter ??= new WKBWriter();

    #region Query Conversions

    /// <summary>
    /// Converts proto QueryFeaturesRequest to domain FeatureQuery.
    /// Used by server to process incoming queries.
    /// </summary>
    public static FeatureQuery ToFeatureQuery(QueryFeaturesRequest request)
    {
        return new FeatureQuery
        {
            Where = string.IsNullOrEmpty(request.Where) ? null : request.Where,
            ObjectIds = request.ObjectIds.Count > 0
                ? request.ObjectIds.ToImmutableArray()
                : null,
            OutFields = request.OutFields.Count > 0
                ? request.OutFields.ToImmutableArray()
                : null,
            ReturnGeometry = request.ReturnGeometry,
            SpatialFilter = request.SpatialFilter != null
                ? ToSpatialFilter(request.SpatialFilter)
                : null,
            Offset = request.ResultOffset > 0 ? request.ResultOffset : null,
            Count = request.ResultRecordCount > 0 ? request.ResultRecordCount : null,
            OrderBy = string.IsNullOrEmpty(request.OrderBy) ? null : request.OrderBy,
            ReturnDistinct = request.ReturnDistinct,
            Statistics = request.OutStatistics.Count > 0
                ? request.OutStatistics.Select(ToStatisticDefinition).ToImmutableArray()
                : null,
            GroupBy = request.GroupBy.Count > 0
                ? request.GroupBy.ToImmutableArray()
                : null
        };
    }

    /// <summary>
    /// Converts domain FeatureQuery to proto QueryFeaturesRequest.
    /// Used by mobile clients to build query requests.
    /// </summary>
    public static QueryFeaturesRequest ToProtoRequest(FeatureQuery query, string serviceId, int layerId)
    {
        var request = new QueryFeaturesRequest
        {
            ServiceId = serviceId,
            LayerId = layerId,
            Where = query.Where ?? string.Empty,
            ReturnGeometry = query.ReturnGeometry,
            ResultOffset = query.Offset ?? 0,
            ResultRecordCount = query.Count ?? 1000,
            OrderBy = query.OrderBy ?? string.Empty,
            ReturnDistinct = query.ReturnDistinct
        };

        if (query.ObjectIds != null)
        {
            request.ObjectIds.AddRange(query.ObjectIds);
        }

        if (query.OutFields != null)
        {
            request.OutFields.AddRange(query.OutFields);
        }

        if (query.SpatialFilter != null)
        {
            request.SpatialFilter = ToProtoSpatialFilter(query.SpatialFilter);
        }

        if (query.Statistics != null)
        {
            request.OutStatistics.AddRange(query.Statistics.Value.Select(ToProtoStatisticDefinition));
        }

        if (query.GroupBy != null)
        {
            request.GroupBy.AddRange(query.GroupBy);
        }

        return request;
    }

    #endregion

    #region Spatial Filter Conversions

    /// <summary>
    /// Converts proto SpatialFilter to domain SpatialFilter.
    /// </summary>
    public static Honua.Shared.Models.SpatialFilter ToSpatialFilter(Geospatial.V1.SpatialFilter protoFilter)
    {
        return new Honua.Shared.Models.SpatialFilter
        {
            FilterGeometry = ToNtsGeometry(protoFilter.Geometry),
            Relationship = ToSpatialRelationship(protoFilter.SpatialRelationship),
            BufferDistance = protoFilter.Distance > 0 ? protoFilter.Distance : null,
            BufferUnit = protoFilter.DistanceUnit != Geospatial.V1.DistanceUnit.Unspecified
                ? ToDistanceUnit(protoFilter.DistanceUnit)
                : null,
            SpatialReference = protoFilter.SpatialReference != null
                ? ToSpatialReference(protoFilter.SpatialReference)
                : null
        };
    }

    /// <summary>
    /// Converts domain SpatialFilter to proto SpatialFilter.
    /// </summary>
    public static Geospatial.V1.SpatialFilter ToProtoSpatialFilter(Honua.Shared.Models.SpatialFilter filter)
    {
        var protoFilter = new Geospatial.V1.SpatialFilter
        {
            Geometry = ToProtoGeometry(filter.FilterGeometry),
            SpatialRelationship = ToProtoSpatialRelationship(filter.Relationship),
            Distance = filter.BufferDistance ?? 0
        };

        if (filter.BufferUnit != null)
        {
            protoFilter.DistanceUnit = ToProtoDistanceUnit(filter.BufferUnit.Value);
        }

        if (filter.SpatialReference != null)
        {
            protoFilter.SpatialReference = ToProtoSpatialReference(filter.SpatialReference);
        }

        return protoFilter;
    }

    #endregion

    #region Geometry Conversions

    /// <summary>
    /// Converts proto geometry to NetTopologySuite geometry.
    /// Shared between server and mobile for consistent spatial operations.
    /// </summary>
    public static NetTopologySuite.Geometries.Geometry ToNtsGeometry(Geospatial.V1.Geometry protoGeometry)
    {
        return protoGeometry.ShapeCase switch
        {
            Geospatial.V1.Geometry.ShapeOneofCase.Point => CreatePoint(protoGeometry.Point),
            Geospatial.V1.Geometry.ShapeOneofCase.MultiPoint => CreateMultiPoint(protoGeometry.MultiPoint),
            Geospatial.V1.Geometry.ShapeOneofCase.Polyline => CreateLineString(protoGeometry.Polyline),
            Geospatial.V1.Geometry.ShapeOneofCase.Polygon => CreatePolygon(protoGeometry.Polygon),
            Geospatial.V1.Geometry.ShapeOneofCase.MultiPolygon => CreateMultiPolygon(protoGeometry.MultiPolygon),
            _ => throw new NotSupportedException($"Geometry type {protoGeometry.ShapeCase} is not supported")
        };
    }

    /// <summary>
    /// Converts NetTopologySuite geometry to proto geometry.
    /// Used for sending spatial data in gRPC responses/requests.
    /// </summary>
    public static Geospatial.V1.Geometry ToProtoGeometry(NetTopologySuite.Geometries.Geometry? geometry)
    {
        if (geometry == null)
            return new Geospatial.V1.Geometry();

        return geometry switch
        {
            Point point => new Geospatial.V1.Geometry
            {
                Point = CreateProtoPoint(point)
            },
            LineString lineString => ToProtoLineString(lineString),
            Polygon polygon => ToProtoPolygon(polygon),
            MultiPoint multiPoint => ToProtoMultiPoint(multiPoint),
            MultiLineString multiLineString => ToProtoMultiLineString(multiLineString),
            MultiPolygon multiPolygon => ToProtoMultiPolygon(multiPolygon),
            _ => throw new NotSupportedException($"Geometry type {geometry.GetType().Name} is not supported")
        };
    }

    #endregion

    #region Helper Methods

    private static NetTopologySuite.Geometries.Point CreatePoint(PointGeometry protoPoint)
    {
        var coord = new NetTopologySuite.Geometries.Coordinate(protoPoint.X, protoPoint.Y);
        if (protoPoint.HasZ)
            coord.Z = protoPoint.Z;
        if (protoPoint.HasM)
            coord.M = protoPoint.M;
        return _geoFactory.CreatePoint(coord);
    }

    private static MultiPoint CreateMultiPoint(MultiPointGeometry protoMultiPoint)
    {
        var points = protoMultiPoint.Points.Select(CreatePoint).ToArray();
        return _geoFactory.CreateMultiPoint(points);
    }

    private static LineString CreateLineString(PolylineGeometry protoPolyline)
    {
        if (protoPolyline.Paths.Count == 0)
            return _geoFactory.CreateLineString();

        var coordinates = protoPolyline.Paths[0].Coords.Select(c =>
        {
            var coord = new NetTopologySuite.Geometries.Coordinate(c.X, c.Y);
            if (c.HasZ)
                coord.Z = c.Z;
            if (c.HasM)
                coord.M = c.M;
            return coord;
        }).ToArray();

        return _geoFactory.CreateLineString(coordinates);
    }

    private static Polygon CreatePolygon(PolygonGeometry protoPolygon)
    {
        if (protoPolygon.Rings.Count == 0)
            return _geoFactory.CreatePolygon();

        var exteriorRing = CreateLinearRing(protoPolygon.Rings[0]);
        var holes = protoPolygon.Rings.Skip(1).Select(CreateLinearRing).ToArray();

        return _geoFactory.CreatePolygon(exteriorRing, holes);
    }

    private static LinearRing CreateLinearRing(Geospatial.V1.CoordinateSequence protoRing)
    {
        var coordinates = protoRing.Coords.Select(c =>
        {
            var coord = new NetTopologySuite.Geometries.Coordinate(c.X, c.Y);
            if (c.HasZ)
                coord.Z = c.Z;
            if (c.HasM)
                coord.M = c.M;
            return coord;
        }).ToArray();

        return _geoFactory.CreateLinearRing(coordinates);
    }

    private static MultiPolygon CreateMultiPolygon(MultiPolygonGeometry protoMultiPolygon)
    {
        var polygons = protoMultiPolygon.Polygons.Select(CreatePolygon).ToArray();
        return _geoFactory.CreateMultiPolygon(polygons);
    }

    // Conversion helper methods for proto types
    private static Honua.Shared.Models.SpatialRelationship ToSpatialRelationship(Geospatial.V1.SpatialRelationship protoRelationship)
    {
        return protoRelationship switch
        {
            Geospatial.V1.SpatialRelationship.Intersects => Honua.Shared.Models.SpatialRelationship.Intersects,
            Geospatial.V1.SpatialRelationship.Contains => Honua.Shared.Models.SpatialRelationship.Contains,
            Geospatial.V1.SpatialRelationship.Within => Honua.Shared.Models.SpatialRelationship.Within,
            Geospatial.V1.SpatialRelationship.Crosses => Honua.Shared.Models.SpatialRelationship.Crosses,
            Geospatial.V1.SpatialRelationship.Touches => Honua.Shared.Models.SpatialRelationship.Touches,
            Geospatial.V1.SpatialRelationship.Overlaps => Honua.Shared.Models.SpatialRelationship.Overlaps,
            Geospatial.V1.SpatialRelationship.Disjoint => Honua.Shared.Models.SpatialRelationship.Disjoint,
            Geospatial.V1.SpatialRelationship.Equals => Honua.Shared.Models.SpatialRelationship.Equals,
            _ => Honua.Shared.Models.SpatialRelationship.Intersects
        };
    }

    private static Honua.Shared.Models.DistanceUnit ToDistanceUnit(Geospatial.V1.DistanceUnit protoUnit)
    {
        return protoUnit switch
        {
            Geospatial.V1.DistanceUnit.Meters => Honua.Shared.Models.DistanceUnit.Meters,
            Geospatial.V1.DistanceUnit.Feet => Honua.Shared.Models.DistanceUnit.Feet,
            Geospatial.V1.DistanceUnit.Kilometers => Honua.Shared.Models.DistanceUnit.Kilometers,
            Geospatial.V1.DistanceUnit.Miles => Honua.Shared.Models.DistanceUnit.Miles,
            _ => Honua.Shared.Models.DistanceUnit.Meters
        };
    }

    private static Honua.Shared.Models.SpatialReference ToSpatialReference(Geospatial.V1.SpatialReference protoSr)
    {
        return new Honua.Shared.Models.SpatialReference
        {
            WKID = protoSr.Wkid > 0 ? protoSr.Wkid : null,
            LatestWKID = protoSr.LatestWkid > 0 ? protoSr.LatestWkid : null,
            WKT = string.IsNullOrEmpty(protoSr.Wkt) ? null : protoSr.Wkt
        };
    }

    private static Honua.Shared.Models.StatisticDefinition ToStatisticDefinition(Geospatial.V1.StatisticDefinition protoStat)
    {
        return new Honua.Shared.Models.StatisticDefinition
        {
            FieldName = protoStat.OnStatisticField,
            StatisticType = ToStatisticType(protoStat.StatisticType),
            OutputFieldName = string.IsNullOrEmpty(protoStat.OutStatisticFieldName) ? null : protoStat.OutStatisticFieldName
        };
    }

    private static Honua.Shared.Models.StatisticType ToStatisticType(Geospatial.V1.StatisticType protoType)
    {
        return protoType switch
        {
            Geospatial.V1.StatisticType.Count => Honua.Shared.Models.StatisticType.Count,
            Geospatial.V1.StatisticType.Sum => Honua.Shared.Models.StatisticType.Sum,
            Geospatial.V1.StatisticType.Min => Honua.Shared.Models.StatisticType.Min,
            Geospatial.V1.StatisticType.Max => Honua.Shared.Models.StatisticType.Max,
            Geospatial.V1.StatisticType.Avg => Honua.Shared.Models.StatisticType.Average,
            Geospatial.V1.StatisticType.Stddev => Honua.Shared.Models.StatisticType.StandardDeviation,
            Geospatial.V1.StatisticType.Var => Honua.Shared.Models.StatisticType.Variance,
            _ => Honua.Shared.Models.StatisticType.Count
        };
    }

    // Reverse conversion methods (domain to proto)
    private static Geospatial.V1.SpatialRelationship ToProtoSpatialRelationship(Honua.Shared.Models.SpatialRelationship relationship)
    {
        return relationship switch
        {
            Honua.Shared.Models.SpatialRelationship.Intersects => Geospatial.V1.SpatialRelationship.Intersects,
            Honua.Shared.Models.SpatialRelationship.Contains => Geospatial.V1.SpatialRelationship.Contains,
            Honua.Shared.Models.SpatialRelationship.Within => Geospatial.V1.SpatialRelationship.Within,
            Honua.Shared.Models.SpatialRelationship.Crosses => Geospatial.V1.SpatialRelationship.Crosses,
            Honua.Shared.Models.SpatialRelationship.Touches => Geospatial.V1.SpatialRelationship.Touches,
            Honua.Shared.Models.SpatialRelationship.Overlaps => Geospatial.V1.SpatialRelationship.Overlaps,
            Honua.Shared.Models.SpatialRelationship.Disjoint => Geospatial.V1.SpatialRelationship.Disjoint,
            Honua.Shared.Models.SpatialRelationship.Equals => Geospatial.V1.SpatialRelationship.Equals,
            _ => Geospatial.V1.SpatialRelationship.Intersects
        };
    }

    private static Geospatial.V1.DistanceUnit ToProtoDistanceUnit(Honua.Shared.Models.DistanceUnit unit)
    {
        return unit switch
        {
            Honua.Shared.Models.DistanceUnit.Meters => Geospatial.V1.DistanceUnit.Meters,
            Honua.Shared.Models.DistanceUnit.Feet => Geospatial.V1.DistanceUnit.Feet,
            Honua.Shared.Models.DistanceUnit.Kilometers => Geospatial.V1.DistanceUnit.Kilometers,
            Honua.Shared.Models.DistanceUnit.Miles => Geospatial.V1.DistanceUnit.Miles,
            _ => Geospatial.V1.DistanceUnit.Meters
        };
    }

    private static Geospatial.V1.SpatialReference ToProtoSpatialReference(Honua.Shared.Models.SpatialReference sr)
    {
        return new Geospatial.V1.SpatialReference
        {
            Wkid = sr.WKID ?? 0,
            LatestWkid = sr.LatestWKID ?? 0,
            Wkt = sr.WKT ?? string.Empty
        };
    }

    private static Geospatial.V1.StatisticDefinition ToProtoStatisticDefinition(Honua.Shared.Models.StatisticDefinition stat)
    {
        return new Geospatial.V1.StatisticDefinition
        {
            OnStatisticField = stat.FieldName,
            StatisticType = ToProtoStatisticType(stat.StatisticType),
            OutStatisticFieldName = stat.OutputFieldName ?? string.Empty
        };
    }

    private static Geospatial.V1.StatisticType ToProtoStatisticType(Honua.Shared.Models.StatisticType type)
    {
        return type switch
        {
            Honua.Shared.Models.StatisticType.Count => Geospatial.V1.StatisticType.Count,
            Honua.Shared.Models.StatisticType.Sum => Geospatial.V1.StatisticType.Sum,
            Honua.Shared.Models.StatisticType.Min => Geospatial.V1.StatisticType.Min,
            Honua.Shared.Models.StatisticType.Max => Geospatial.V1.StatisticType.Max,
            Honua.Shared.Models.StatisticType.Average => Geospatial.V1.StatisticType.Avg,
            Honua.Shared.Models.StatisticType.StandardDeviation => Geospatial.V1.StatisticType.Stddev,
            Honua.Shared.Models.StatisticType.Variance => Geospatial.V1.StatisticType.Var,
            _ => Geospatial.V1.StatisticType.Count
        };
    }

    #endregion

    #region Proto Geometry Conversion Helpers

    private static Geospatial.V1.Geometry ToProtoLineString(LineString lineString)
    {
        var polyline = new PolylineGeometry();
        var path = new Geospatial.V1.CoordinateSequence();

        foreach (var coord in lineString.Coordinates)
        {
            path.Coords.Add(CreateProtoCoordinate(coord));
        }

        polyline.Paths.Add(path);
        return new Geospatial.V1.Geometry { Polyline = polyline };
    }

    private static Geospatial.V1.Geometry ToProtoPolygon(Polygon polygon)
    {
        var protoPolygon = new PolygonGeometry();

        // Add exterior ring
        if (polygon.ExteriorRing != null)
        {
            protoPolygon.Rings.Add(ToProtoCoordinateSequence(polygon.ExteriorRing));
        }

        // Add holes
        for (int i = 0; i < polygon.NumInteriorRings; i++)
        {
            var hole = polygon.GetInteriorRingN(i);
            protoPolygon.Rings.Add(ToProtoCoordinateSequence(hole));
        }

        return new Geospatial.V1.Geometry { Polygon = protoPolygon };
    }

    private static Geospatial.V1.Geometry ToProtoMultiPoint(MultiPoint multiPoint)
    {
        var protoMultiPoint = new MultiPointGeometry();

        for (int i = 0; i < multiPoint.NumGeometries; i++)
        {
            var point = (Point)multiPoint.GetGeometryN(i);
            protoMultiPoint.Points.Add(CreateProtoPoint(point));
        }

        return new Geospatial.V1.Geometry { MultiPoint = protoMultiPoint };
    }

    private static Geospatial.V1.Geometry ToProtoMultiLineString(MultiLineString multiLineString)
    {
        var polyline = new PolylineGeometry();

        for (int i = 0; i < multiLineString.NumGeometries; i++)
        {
            var lineString = (LineString)multiLineString.GetGeometryN(i);
            polyline.Paths.Add(ToProtoCoordinateSequence(lineString));
        }

        return new Geospatial.V1.Geometry { Polyline = polyline };
    }

    private static Geospatial.V1.Geometry ToProtoMultiPolygon(MultiPolygon multiPolygon)
    {
        var protoMultiPolygon = new MultiPolygonGeometry();

        for (int i = 0; i < multiPolygon.NumGeometries; i++)
        {
            var polygon = (Polygon)multiPolygon.GetGeometryN(i);
            var protoPolygon = new PolygonGeometry();

            // Add exterior ring
            if (polygon.ExteriorRing != null)
            {
                protoPolygon.Rings.Add(ToProtoCoordinateSequence(polygon.ExteriorRing));
            }

            // Add holes
            for (int j = 0; j < polygon.NumInteriorRings; j++)
            {
                var hole = polygon.GetInteriorRingN(j);
                protoPolygon.Rings.Add(ToProtoCoordinateSequence(hole));
            }

            protoMultiPolygon.Polygons.Add(protoPolygon);
        }

        return new Geospatial.V1.Geometry { MultiPolygon = protoMultiPolygon };
    }

    private static Geospatial.V1.CoordinateSequence ToProtoCoordinateSequence(LineString lineString)
    {
        var coordSeq = new Geospatial.V1.CoordinateSequence();

        foreach (var coord in lineString.Coordinates)
        {
            coordSeq.Coords.Add(CreateProtoCoordinate(coord));
        }

        return coordSeq;
    }

    private static PointGeometry CreateProtoPoint(Point point)
    {
        var protoPoint = new PointGeometry { X = point.X, Y = point.Y };
        if (!double.IsNaN(point.Z))
            protoPoint.Z = point.Z;
        if (!double.IsNaN(point.M))
            protoPoint.M = point.M;
        return protoPoint;
    }

    private static Geospatial.V1.Coordinate CreateProtoCoordinate(NetTopologySuite.Geometries.Coordinate coord)
    {
        var protoCoord = new Geospatial.V1.Coordinate { X = coord.X, Y = coord.Y };
        if (!double.IsNaN(coord.Z))
            protoCoord.Z = coord.Z;
        if (!double.IsNaN(coord.M))
            protoCoord.M = coord.M;
        return protoCoord;
    }

    #endregion
}