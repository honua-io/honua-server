// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Models;
using Honua.Mobile.Core.Proto;

namespace Honua.Mobile.Core.Converters;

/// <summary>
/// Converts between mobile SDK models and proto messages.
/// </summary>
internal static class ProtoConverters
{
    /// <summary>
    /// Converts mobile FeatureQuery to proto QueryFeaturesRequest.
    /// </summary>
    public static QueryFeaturesRequest ToProtoRequest(string serviceId, int layerId, Models.FeatureQuery query)
    {
        var request = new QueryFeaturesRequest
        {
            ServiceId = serviceId,
            LayerId = layerId,
            Where = query.Where ?? string.Empty,
            ReturnGeometry = query.ReturnGeometry,
            ResultOffset = query.Offset ?? 0,
            ResultRecordCount = query.Limit ?? 0,
            OrderBy = query.OrderBy ?? string.Empty,
            ReturnDistinct = query.Distinct
        };

        if (query.ObjectIds != null)
        {
            request.ObjectIds.AddRange(query.ObjectIds);
        }

        if (query.OutFields != null)
        {
            request.OutFields.AddRange(query.OutFields);
        }

        if (query.OutputSpatialReference != null)
        {
            request.OutSr = ToProtoSpatialReference(query.OutputSpatialReference);
        }

        if (query.SpatialFilter != null)
        {
            request.SpatialFilter = ToProtoSpatialFilter(query.SpatialFilter);
        }

        if (query.Statistics != null)
        {
            request.OutStatistics.AddRange(query.Statistics.Select(ToProtoStatistic));
        }

        if (query.GroupByFields != null)
        {
            request.GroupBy.AddRange(query.GroupByFields);
        }

        return request;
    }

    /// <summary>
    /// Converts proto QueryFeaturesResponse to mobile QueryResult.
    /// </summary>
    public static Models.QueryResult<Models.Feature> FromProtoResponse(QueryFeaturesResponse response)
    {
        var features = response.Features.Select(FromProtoFeature).ToList();
        var fields = response.Fields.Select(FromProtoField).ToList();

        return new Models.QueryResult<Models.Feature>
        {
            Items = features,
            ObjectIdFieldName = response.ObjectIdFieldName,
            GeometryType = FromProtoGeometryType(response.GeometryType),
            SpatialReference = response.SpatialReference != null
                ? FromProtoSpatialReference(response.SpatialReference)
                : null,
            Fields = fields,
            HasMoreResults = response.ExceededTransferLimit,
            Count = response.Count > 0 ? response.Count : null,
            ObjectIds = response.ObjectIds.Count > 0 ? response.ObjectIds.ToList() : null,
            Extent = response.Extent != null ? FromProtoExtent(response.Extent) : null
        };
    }

    /// <summary>
    /// Converts proto Feature to mobile Feature.
    /// </summary>
    public static Models.Feature FromProtoFeature(Proto.Feature protoFeature)
    {
        var attributes = protoFeature.Attributes.ToDictionary(
            kvp => kvp.Key,
            kvp => FromProtoAttributeValue(kvp.Value));

        return Models.Feature.Create(
            protoFeature.Id,
            attributes,
            protoFeature.Geometry != null ? FromProtoGeometry(protoFeature.Geometry) : null);
    }

    /// <summary>
    /// Converts mobile Feature to proto Feature.
    /// </summary>
    public static Proto.Feature ToProtoFeature(Models.Feature feature)
    {
        var protoFeature = new Proto.Feature
        {
            Id = feature.Id
        };

        foreach (var attr in feature.Attributes)
        {
            protoFeature.Attributes[attr.Key] = ToProtoAttributeValue(attr.Value);
        }

        if (feature.Geometry != null)
        {
            protoFeature.Geometry = ToProtoGeometry(feature.Geometry);
        }

        return protoFeature;
    }

    /// <summary>
    /// Converts proto AttributeValue to object.
    /// </summary>
    private static object? FromProtoAttributeValue(AttributeValue protoValue)
    {
        return protoValue.ValueCase switch
        {
            AttributeValue.ValueOneofCase.StringValue => protoValue.StringValue,
            AttributeValue.ValueOneofCase.Int32Value => protoValue.Int32Value,
            AttributeValue.ValueOneofCase.Int64Value => protoValue.Int64Value,
            AttributeValue.ValueOneofCase.DoubleValue => protoValue.DoubleValue,
            AttributeValue.ValueOneofCase.FloatValue => protoValue.FloatValue,
            AttributeValue.ValueOneofCase.BoolValue => protoValue.BoolValue,
            AttributeValue.ValueOneofCase.DatetimeValue => DateTimeOffset.FromUnixTimeMilliseconds(protoValue.DatetimeValue),
            AttributeValue.ValueOneofCase.BytesValue => protoValue.BytesValue.ToByteArray(),
            AttributeValue.ValueOneofCase.NullValue => null,
            _ => null
        };
    }

    /// <summary>
    /// Converts object to proto AttributeValue.
    /// </summary>
    private static AttributeValue ToProtoAttributeValue(object? value)
    {
        var protoValue = new AttributeValue();

        switch (value)
        {
            case null:
                protoValue.NullValue = NullValue.NullValue;
                break;
            case string str:
                protoValue.StringValue = str;
                break;
            case int i:
                protoValue.Int32Value = i;
                break;
            case long l:
                protoValue.Int64Value = l;
                break;
            case double d:
                protoValue.DoubleValue = d;
                break;
            case float f:
                protoValue.FloatValue = f;
                break;
            case bool b:
                protoValue.BoolValue = b;
                break;
            case DateTime dt:
                protoValue.DatetimeValue = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                break;
            case DateTimeOffset dto:
                protoValue.DatetimeValue = dto.ToUnixTimeMilliseconds();
                break;
            case byte[] bytes:
                protoValue.BytesValue = Google.Protobuf.ByteString.CopyFrom(bytes);
                break;
            default:
                protoValue.StringValue = value.ToString() ?? string.Empty;
                break;
        }

        return protoValue;
    }

    private static Models.SpatialReference FromProtoSpatialReference(Proto.SpatialReference proto)
    {
        return new Models.SpatialReference
        {
            Wkid = proto.Wkid > 0 ? proto.Wkid : null,
            LatestWkid = proto.LatestWkid > 0 ? proto.LatestWkid : null,
            Wkt = !string.IsNullOrWhiteSpace(proto.Wkt) ? proto.Wkt : null
        };
    }

    private static Proto.SpatialReference ToProtoSpatialReference(Models.SpatialReference sr)
    {
        return new Proto.SpatialReference
        {
            Wkid = sr.Wkid ?? 0,
            LatestWkid = sr.LatestWkid ?? 0,
            Wkt = sr.Wkt ?? string.Empty
        };
    }

    private static Models.GeometryType FromProtoGeometryType(Proto.GeometryType protoType)
    {
        return protoType switch
        {
            Proto.GeometryType.Point => Models.GeometryType.Point,
            Proto.GeometryType.MultiPoint => Models.GeometryType.MultiPoint,
            Proto.GeometryType.LineString => Models.GeometryType.LineString,
            Proto.GeometryType.MultiLineString => Models.GeometryType.MultiLineString,
            Proto.GeometryType.Polygon => Models.GeometryType.Polygon,
            Proto.GeometryType.MultiPolygon => Models.GeometryType.MultiPolygon,
            Proto.GeometryType.GeometryCollection => Models.GeometryType.GeometryCollection,
            Proto.GeometryType.None => Models.GeometryType.None,
            _ => Models.GeometryType.None
        };
    }

    private static Models.FieldDefinition FromProtoField(Proto.FieldDefinition protoField)
    {
        return new Models.FieldDefinition
        {
            Name = protoField.Name,
            Type = FromProtoFieldType(protoField.FieldType),
            Length = protoField.Length > 0 ? protoField.Length : null,
            Nullable = protoField.Nullable
        };
    }

    private static Models.FieldType FromProtoFieldType(Proto.FieldType protoType)
    {
        return protoType switch
        {
            Proto.FieldType.String => Models.FieldType.String,
            Proto.FieldType.Integer => Models.FieldType.Integer,
            Proto.FieldType.BigInteger => Models.FieldType.BigInteger,
            Proto.FieldType.Double => Models.FieldType.Double,
            Proto.FieldType.Float => Models.FieldType.Float,
            Proto.FieldType.Boolean => Models.FieldType.Boolean,
            Proto.FieldType.DateTime => Models.FieldType.DateTime,
            Proto.FieldType.Date => Models.FieldType.Date,
            Proto.FieldType.Time => Models.FieldType.Time,
            Proto.FieldType.Geometry => Models.FieldType.Geometry,
            Proto.FieldType.Json => Models.FieldType.Json,
            Proto.FieldType.Binary => Models.FieldType.Binary,
            Proto.FieldType.Uuid => Models.FieldType.Uuid,
            _ => Models.FieldType.String
        };
    }

    public static Models.Extent FromProtoExtent(Proto.Extent protoExtent)
    {
        return Models.Extent.Create(
            protoExtent.Xmin,
            protoExtent.Ymin,
            protoExtent.Xmax,
            protoExtent.Ymax,
            protoExtent.SpatialReference != null
                ? FromProtoSpatialReference(protoExtent.SpatialReference)
                : null);
    }

    // Additional geometry conversion methods would go here
    // For now, returning simple implementations
    private static Models.Geometry? FromProtoGeometry(Proto.Geometry protoGeometry)
    {
        // TODO: Implement full geometry conversion
        return protoGeometry.ShapeCase switch
        {
            Proto.Geometry.ShapeOneofCase.Point => FromProtoPointGeometry(protoGeometry.Point),
            _ => null
        };
    }

    private static Models.PointGeometry FromProtoPointGeometry(Proto.PointGeometry protoPoint)
    {
        return Models.PointGeometry.Create(protoPoint.X, protoPoint.Y, protoPoint.Z, protoPoint.M);
    }

    private static Proto.Geometry ToProtoGeometry(Models.Geometry geometry)
    {
        // TODO: Implement full geometry conversion
        return geometry switch
        {
            Models.PointGeometry point => new Proto.Geometry { Point = ToProtoPointGeometry(point) },
            _ => new Proto.Geometry()
        };
    }

    private static Proto.PointGeometry ToProtoPointGeometry(Models.PointGeometry point)
    {
        return new Proto.PointGeometry
        {
            X = point.X,
            Y = point.Y,
            Z = point.Z ?? 0,
            M = point.M ?? 0
        };
    }

    private static Proto.SpatialFilter ToProtoSpatialFilter(Models.SpatialFilter filter)
    {
        return new Proto.SpatialFilter
        {
            Geometry = ToProtoGeometry(filter.Geometry),
            SpatialRelationship = ToProtoSpatialRelationship(filter.Relationship),
            SpatialReference = filter.SpatialReference != null
                ? ToProtoSpatialReference(filter.SpatialReference)
                : null,
            Distance = filter.Distance ?? 0,
            DistanceUnit = ToProtoDistanceUnit(filter.DistanceUnit),
            NearestCount = filter.NearestCount ?? 0,
            ReturnDistance = filter.ReturnDistance
        };
    }

    private static Proto.SpatialRelationship ToProtoSpatialRelationship(Models.SpatialRelationship relationship)
    {
        return relationship switch
        {
            Models.SpatialRelationship.Intersects => Proto.SpatialRelationship.Intersects,
            Models.SpatialRelationship.Within => Proto.SpatialRelationship.Within,
            Models.SpatialRelationship.Contains => Proto.SpatialRelationship.Contains,
            Models.SpatialRelationship.EnvelopeIntersects => Proto.SpatialRelationship.EnvelopeIntersects,
            Models.SpatialRelationship.Crosses => Proto.SpatialRelationship.Crosses,
            Models.SpatialRelationship.Touches => Proto.SpatialRelationship.Touches,
            Models.SpatialRelationship.Overlaps => Proto.SpatialRelationship.Overlaps,
            Models.SpatialRelationship.Disjoint => Proto.SpatialRelationship.Disjoint,
            Models.SpatialRelationship.Equals => Proto.SpatialRelationship.Equals,
            Models.SpatialRelationship.WithinDistance => Proto.SpatialRelationship.WithinDistance,
            Models.SpatialRelationship.BeyondDistance => Proto.SpatialRelationship.BeyondDistance,
            Models.SpatialRelationship.NearestNeighbor => Proto.SpatialRelationship.NearestNeighbor,
            _ => Proto.SpatialRelationship.Intersects
        };
    }

    private static Proto.DistanceUnit ToProtoDistanceUnit(Models.DistanceUnit unit)
    {
        return unit switch
        {
            Models.DistanceUnit.Meters => Proto.DistanceUnit.Meters,
            Models.DistanceUnit.Feet => Proto.DistanceUnit.Feet,
            Models.DistanceUnit.Kilometers => Proto.DistanceUnit.Kilometers,
            Models.DistanceUnit.Miles => Proto.DistanceUnit.Miles,
            _ => Proto.DistanceUnit.Meters
        };
    }

    private static Proto.StatisticDefinition ToProtoStatistic(Models.StatisticDefinition stat)
    {
        return new Proto.StatisticDefinition
        {
            OnStatisticField = stat.Field,
            StatisticType = ToProtoStatisticType(stat.Type),
            OutStatisticFieldName = stat.OutputFieldName
        };
    }

    private static Proto.StatisticType ToProtoStatisticType(Models.StatisticType type)
    {
        return type switch
        {
            Models.StatisticType.Count => Proto.StatisticType.Count,
            Models.StatisticType.Sum => Proto.StatisticType.Sum,
            Models.StatisticType.Min => Proto.StatisticType.Min,
            Models.StatisticType.Max => Proto.StatisticType.Max,
            Models.StatisticType.Average => Proto.StatisticType.Avg,
            Models.StatisticType.StandardDeviation => Proto.StatisticType.Stddev,
            Models.StatisticType.Variance => Proto.StatisticType.Var,
            _ => Proto.StatisticType.Count
        };
    }

    /// <summary>
    /// Converts proto ApplyEditsResponse to mobile EditResult.
    /// </summary>
    public static Models.EditResult FromProtoEditResponse(Proto.ApplyEditsResponse response)
    {
        return new Models.EditResult
        {
            CreateResults = response.AddResults.Select(FromProtoEditResult).ToList(),
            UpdateResults = response.UpdateResults.Select(FromProtoEditResult).ToList(),
            DeleteResults = response.DeleteResults.Select(FromProtoEditResult).ToList(),
            Error = response.Error != null ? FromProtoEditError(response.Error) : null
        };
    }

    private static Models.OperationResult FromProtoEditResult(Proto.EditResult protoResult)
    {
        return new Models.OperationResult
        {
            ObjectId = protoResult.ObjectId,
            Success = protoResult.Success,
            Error = protoResult.Error != null ? FromProtoEditError(protoResult.Error) : null
        };
    }

    private static Models.EditError FromProtoEditError(Proto.EditError protoError)
    {
        return new Models.EditError
        {
            Code = protoError.Code,
            Message = protoError.Message
        };
    }
}