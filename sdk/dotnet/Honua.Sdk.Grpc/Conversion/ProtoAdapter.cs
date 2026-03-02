// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Sdk.Grpc.Conversion;

/// <summary>
/// Converts between proto-generated types and SDK domain models.
/// </summary>
internal static class ProtoAdapter
{
    /// <summary>
    /// Converts a domain query request to a proto request.
    /// </summary>
    public static Proto.QueryFeaturesRequest ToProtoRequest(Models.QueryFeaturesRequest request)
    {
        var proto = new Proto.QueryFeaturesRequest
        {
            ServiceId = request.ServiceId,
            LayerId = request.LayerId,
            Where = request.Where,
            ReturnGeometry = request.ReturnGeometry,
            ResultOffset = request.ResultOffset,
            ResultRecordCount = request.ResultRecordCount,
            OrderBy = request.OrderBy,
            ReturnDistinct = request.ReturnDistinct,
            ReturnCountOnly = request.ReturnCountOnly,
            ReturnIdsOnly = request.ReturnIdsOnly,
            ReturnExtentOnly = request.ReturnExtentOnly,
            GeometryPrecision = request.GeometryPrecision,
            MaxAllowableOffset = request.MaxAllowableOffset,
        };

        if (request.ObjectIds is not null)
        {
            proto.ObjectIds.AddRange(request.ObjectIds);
        }

        if (request.OutFields is not null)
        {
            proto.OutFields.AddRange(request.OutFields);
        }

        if (request.OutSr is not null)
        {
            proto.OutSr = new Proto.SpatialReference
            {
                Wkid = request.OutSr.Wkid,
                LatestWkid = request.OutSr.LatestWkid,
                Wkt = request.OutSr.Wkt,
            };
        }

        if (request.OutStatistics is not null)
        {
            foreach (var stat in request.OutStatistics)
            {
                proto.OutStatistics.Add(new Proto.StatisticDefinition
                {
                    OnStatisticField = stat.OnStatisticField,
                    StatisticType = (Proto.StatisticType)stat.StatisticType,
                    OutStatisticFieldName = stat.OutStatisticFieldName,
                });
            }
        }

        if (request.GroupBy is not null)
        {
            proto.GroupBy.AddRange(request.GroupBy);
        }

        return proto;
    }

    /// <summary>
    /// Converts a proto query response to a domain response.
    /// </summary>
    public static Models.QueryFeaturesResponse FromProtoResponse(Proto.QueryFeaturesResponse response)
    {
        return new Models.QueryFeaturesResponse
        {
            ObjectIdFieldName = response.ObjectIdFieldName,
            GeometryType = (Models.GeometryType)response.GeometryType,
            SpatialReference = response.SpatialReference is not null ? ConvertSpatialReference(response.SpatialReference) : null,
            Fields = response.Fields.Select(ConvertField).ToList(),
            Features = response.Features.Select(ConvertFeature).ToList(),
            ExceededTransferLimit = response.ExceededTransferLimit,
            Count = response.Count,
            ObjectIds = response.ObjectIds.ToList(),
            Extent = response.Extent is not null ? ConvertExtent(response.Extent) : null,
        };
    }

    /// <summary>
    /// Converts a proto feature page to a domain feature page.
    /// </summary>
    public static Models.FeaturePage FromProtoPage(Proto.FeaturePage page)
    {
        return new Models.FeaturePage
        {
            ObjectIdFieldName = page.ObjectIdFieldName,
            GeometryType = (Models.GeometryType)page.GeometryType,
            SpatialReference = page.SpatialReference is not null ? ConvertSpatialReference(page.SpatialReference) : null,
            Fields = page.Fields.Select(ConvertField).ToList(),
            Features = page.Features.Select(ConvertFeature).ToList(),
            IsLastPage = page.IsLastPage,
        };
    }

    internal static Models.Feature ConvertFeature(Proto.Feature feature)
    {
        var attributes = new Dictionary<string, object?>();
        foreach (var kvp in feature.Attributes)
        {
            attributes[kvp.Key] = ConvertAttribute(kvp.Value);
        }

        return new Models.Feature
        {
            Id = feature.Id,
            Attributes = attributes,
            Geometry = feature.Geometry is not null ? ConvertGeometry(feature.Geometry) : null,
        };
    }

    internal static object? ConvertAttribute(Proto.AttributeValue attr)
    {
        return attr.ValueCase switch
        {
            Proto.AttributeValue.ValueOneofCase.StringValue => attr.StringValue,
            Proto.AttributeValue.ValueOneofCase.Int32Value => attr.Int32Value,
            Proto.AttributeValue.ValueOneofCase.Int64Value => attr.Int64Value,
            Proto.AttributeValue.ValueOneofCase.DoubleValue => attr.DoubleValue,
            Proto.AttributeValue.ValueOneofCase.FloatValue => (double)attr.FloatValue,
            Proto.AttributeValue.ValueOneofCase.BoolValue => attr.BoolValue,
            Proto.AttributeValue.ValueOneofCase.DatetimeValue => attr.DatetimeValue,
            Proto.AttributeValue.ValueOneofCase.BytesValue => null,
            Proto.AttributeValue.ValueOneofCase.NullValue => null,
            Proto.AttributeValue.ValueOneofCase.None => null,
            _ => null,
        };
    }

    internal static IReadOnlyDictionary<string, object?>? ConvertGeometry(Proto.Geometry geometry)
    {
        return geometry.ShapeCase switch
        {
            Proto.Geometry.ShapeOneofCase.Point => ConvertPoint(geometry.Point),
            Proto.Geometry.ShapeOneofCase.MultiPoint => ConvertMultiPoint(geometry.MultiPoint),
            Proto.Geometry.ShapeOneofCase.Polyline => ConvertPolyline(geometry.Polyline),
            Proto.Geometry.ShapeOneofCase.Polygon => ConvertPolygon(geometry.Polygon),
            Proto.Geometry.ShapeOneofCase.MultiPolygon => ConvertMultiPolygon(geometry.MultiPolygon),
            _ => null,
        };
    }

    private static Dictionary<string, object?> ConvertPoint(Proto.PointGeometry point)
    {
        var result = new Dictionary<string, object?>
        {
            ["x"] = point.X,
            ["y"] = point.Y,
        };
        if (point.HasZ)
            result["z"] = point.Z;
        if (point.HasM)
            result["m"] = point.M;
        return result;
    }

    private static Dictionary<string, object?> ConvertMultiPoint(Proto.MultiPointGeometry multiPoint)
    {
        var points = new List<object?>();
        foreach (var p in multiPoint.Points)
        {
            var coords = new List<object?> { p.X, p.Y };
            if (p.HasZ)
                coords.Add(p.Z);
            points.Add(coords);
        }
        return new Dictionary<string, object?> { ["points"] = points };
    }

    private static Dictionary<string, object?> ConvertPolyline(Proto.PolylineGeometry polyline)
    {
        var paths = new List<object?>();
        foreach (var path in polyline.Paths)
        {
            var coords = new List<object?>();
            foreach (var c in path.Coords)
            {
                var coord = new List<object?> { c.X, c.Y };
                if (c.HasZ)
                    coord.Add(c.Z);
                coords.Add(coord);
            }
            paths.Add(coords);
        }
        return new Dictionary<string, object?> { ["paths"] = paths };
    }

    private static Dictionary<string, object?> ConvertPolygon(Proto.PolygonGeometry polygon)
    {
        var rings = new List<object?>();
        foreach (var ring in polygon.Rings)
        {
            var coords = new List<object?>();
            foreach (var c in ring.Coords)
            {
                var coord = new List<object?> { c.X, c.Y };
                if (c.HasZ)
                    coord.Add(c.Z);
                coords.Add(coord);
            }
            rings.Add(coords);
        }
        return new Dictionary<string, object?> { ["rings"] = rings };
    }

    private static Dictionary<string, object?> ConvertMultiPolygon(Proto.MultiPolygonGeometry multiPolygon)
    {
        var rings = new List<object?>();
        foreach (var poly in multiPolygon.Polygons)
        {
            foreach (var ring in poly.Rings)
            {
                var coords = new List<object?>();
                foreach (var c in ring.Coords)
                {
                    var coord = new List<object?> { c.X, c.Y };
                    if (c.HasZ)
                        coord.Add(c.Z);
                    coords.Add(coord);
                }
                rings.Add(coords);
            }
        }
        return new Dictionary<string, object?> { ["rings"] = rings };
    }

    private static Models.SpatialReference ConvertSpatialReference(Proto.SpatialReference sr)
    {
        return new Models.SpatialReference
        {
            Wkid = sr.Wkid,
            LatestWkid = sr.LatestWkid,
            Wkt = sr.Wkt,
        };
    }

    private static Models.Extent ConvertExtent(Proto.Extent extent)
    {
        return new Models.Extent
        {
            Xmin = extent.Xmin,
            Ymin = extent.Ymin,
            Xmax = extent.Xmax,
            Ymax = extent.Ymax,
            SpatialReference = extent.SpatialReference is not null ? ConvertSpatialReference(extent.SpatialReference) : null,
        };
    }

    private static Models.FieldDefinition ConvertField(Proto.FieldDefinition field)
    {
        return new Models.FieldDefinition
        {
            Name = field.Name,
            FieldType = (Models.FieldType)field.FieldType,
            Length = field.Length,
            Nullable = field.Nullable,
        };
    }
}
