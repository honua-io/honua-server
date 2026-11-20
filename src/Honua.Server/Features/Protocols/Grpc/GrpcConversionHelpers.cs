// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Google.Protobuf.Collections;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using DomainDistanceUnit = Honua.Core.Features.FeatureStore.Domain.DistanceUnit;
using DomainSpatialFilter = Honua.Core.Features.FeatureStore.Domain.SpatialFilter;
using DomainSpatialRelationship = Honua.Core.Features.FeatureStore.Domain.SpatialRelationship;
using Proto = Geospatial.V1;

namespace Honua.Server.Features.Protocols.Grpc;

internal static class GrpcConversionHelpers
{
    private static readonly GeometryFactory _geoFactory = new();

    [ThreadStatic]
    private static WKBReader? _wkbReader;
    [ThreadStatic]
    private static WKBWriter? _wkbWriter;

    private static WKBReader WkbReader => _wkbReader ??= new WKBReader();
    private static WKBWriter WkbWriter => _wkbWriter ??= new WKBWriter();

    public static int NarrowPagination(long value, string fieldName)
    {
        if (value is > int.MaxValue or < int.MinValue)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"{fieldName} must be between {int.MinValue} and {int.MaxValue}."));
        }

        return (int)value;
    }

    public static FeatureQuery ToFeatureQuery(Proto.QueryFeaturesRequest request)
    {
        var query = new FeatureQuery
        {
            Where = string.IsNullOrEmpty(request.Where) ? null : request.Where,
            ObjectIds = request.ObjectIds.Count > 0
                ? request.ObjectIds.ToImmutableArray()
                : null,
            OutFields = NormalizeOutFields(request.OutFields),
            Offset = NarrowPagination(request.ResultOffsetLong, "result_offset_long") is int o and > 0 ? o : null,
            Limit = NarrowPagination(request.ResultRecordCountLong, "result_record_count_long") is int l and > 0 ? l : null,
            OutputSrid = TryResolveOutputSrid(request.OutSr),
            Distinct = request.ReturnDistinct,
            SpatialFilter = request.SpatialFilter != null
                ? ToSpatialFilter(request.SpatialFilter)
                : null,
        };

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

    private static ImmutableArray<string>? NormalizeOutFields(RepeatedField<string> outFields)
    {
        if (outFields.Count == 0)
        {
            return null;
        }

        var fields = outFields
            .SelectMany(static field => field.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(static field => field.Length > 0)
            .ToImmutableArray();

        return fields.IsDefaultOrEmpty ||
            fields.Any(static field => string.Equals(field, "*", StringComparison.Ordinal))
                ? null
                : fields;
    }

    public static FeatureEditBatch ToFeatureEditBatch(Proto.ApplyEditsRequest request)
    {
        GrpcGlobalIds.EnsureSelectorMode(request);
        var creates = request.Adds.Count == 0
            ? ImmutableArray<Feature>.Empty
            : request.Adds.Select(feature => ToDomainFeature(feature, requireId: false)).ToImmutableArray();

        var updates = request.Updates.Count == 0
            ? ImmutableArray<Feature>.Empty
            : request.Updates.Select(feature => ToDomainFeature(feature, requireId: !request.UseGlobalIds)).ToImmutableArray();

        // Global-ID deletes count as deletes for authorization, capabilities and limits now;
        // the placeholders are replaced by resolved object IDs before any read or write.
        var deletes = request.UseGlobalIds
            ? ImmutableArray.CreateRange(Enumerable.Repeat(0L, request.DeleteGlobalIds.Count))
            : request.Deletes.ToImmutableArray();

        return FeatureEditBatch.Create(
            creates: creates,
            updates: updates,
            deletes: deletes,
            rollbackOnFailure: request.RollbackOnFailure,
            useGlobalIds: false);
    }

    public static Proto.ApplyEditsResponse ToProtoApplyEditsResponse(FeatureEditResult result)
    {
        var response = new Proto.ApplyEditsResponse();

        if (!result.CreateResults.IsDefaultOrEmpty)
        {
            response.AddResults.AddRange(result.CreateResults.Select(ToProtoEditResult));
        }

        if (!result.UpdateResults.IsDefaultOrEmpty)
        {
            response.UpdateResults.AddRange(result.UpdateResults.Select(ToProtoEditResult));
        }

        if (!result.DeleteResults.IsDefaultOrEmpty)
        {
            response.DeleteResults.AddRange(result.DeleteResults.Select(ToProtoEditResult));
        }

        if (result.WasRolledBack && (response.AddResults.Count > 0 || response.UpdateResults.Count > 0 || response.DeleteResults.Count > 0))
        {
            response.Error = new Proto.ErrorDetail
            {
                Code = 1000,
                Message = "Operation rolled back.",
                Category = Proto.ErrorCategory.Execution
            };
        }

        return response;
    }

    public static int? TryResolveOutputSrid(Proto.SpatialReference? spatialReference)
    {
        if (spatialReference == null)
        {
            return null;
        }

        if (spatialReference.LatestWkid > 0)
        {
            return spatialReference.LatestWkid;
        }

        if (spatialReference.Wkid > 0)
        {
            return spatialReference.Wkid;
        }

        return null;
    }

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

    public static Proto.Feature ToProtoFeature(
        Feature feature,
        bool includeGeometry = true,
        GeometryLimits? geometryLimits = null,
        string? objectIdFieldName = null,
        IReadOnlySet<string>? hiddenFields = null,
        IReadOnlyList<MetadataV2Field>? fields = null)
    {
        var proto = new Proto.Feature { Id = feature.Id };

        foreach (var (key, value) in feature.Attributes)
        {
            if (FeatureAttributeVisibility.IsInternalAttribute(key) || hiddenFields?.Contains(key) == true)
            {
                continue;
            }

            if (objectIdFieldName is { Length: > 0 } && IsRepeatedObjectId(key, value, feature.Id, objectIdFieldName))
            {
                continue;
            }

            var field = fields?.FirstOrDefault(field => string.Equals(field.Name, key, StringComparison.OrdinalIgnoreCase));
            proto.Attributes[key] = ToAttributeValue(value, field?.Type);
        }

        if (includeGeometry && feature.Geometry is { Length: > 0 })
        {
            var geometry = ToProtoGeometry(feature.Geometry, geometryLimits);
            if (geometry.ShapeCase != Proto.Geometry.ShapeOneofCase.None)
            {
                proto.Geometry = geometry;
            }
        }

        return proto;
    }

    private const string StorageObjectIdColumn = "objectid";

    private static bool IsRepeatedObjectId(string key, object? value, long featureId, string objectIdFieldName)
    {
        if (featureId == 0
            || !(string.Equals(key, objectIdFieldName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, StorageObjectIdColumn, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return value switch
        {
            long number => number == featureId,
            int number => number == featureId,
            short number => number == featureId,
            decimal number => number == featureId,
            string text => long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed == featureId,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } element
                => element.TryGetInt64(out var parsed) && parsed == featureId,
            _ => false,
        };
    }

    public static Proto.FieldDefinition ToProtoField(MetadataV2Field field)
    {
        return new Proto.FieldDefinition
        {
            Name = field.Name,
            FieldType = ToProtoFieldType(field.Type),
            Length = field.Length ?? 0,
            Nullable = field.Nullable,
            // Same display-name precedence as GeoServices REST field metadata
            // (FeatureServerUtilities.V2 / QueryFormatters).
            Alias = field.Alias ?? field.Title ?? field.Name
        };
    }

    public static Proto.SpatialReference ToProtoSpatialReference(SpatialReference sr)
    {
        return new Proto.SpatialReference
        {
            Wkid = sr.Wkid,
            LatestWkid = sr.LatestWkid ?? 0,
            Wkt = sr.Wkt ?? string.Empty
        };
    }

    public static Proto.GeometryType ToProtoGeometryType(MetadataV2GeometryType geometryType)
    {
        return geometryType switch
        {
            MetadataV2GeometryType.Point => Proto.GeometryType.Point,
            MetadataV2GeometryType.MultiPoint => Proto.GeometryType.MultiPoint,
            MetadataV2GeometryType.LineString => Proto.GeometryType.LineString,
            MetadataV2GeometryType.MultiLineString => Proto.GeometryType.MultiLineString,
            MetadataV2GeometryType.Polygon => Proto.GeometryType.Polygon,
            MetadataV2GeometryType.MultiPolygon => Proto.GeometryType.MultiPolygon,
            MetadataV2GeometryType.GeometryCollection => Proto.GeometryType.GeometryCollection,
            MetadataV2GeometryType.None => Proto.GeometryType.None,
            _ => Proto.GeometryType.Unspecified
        };
    }

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

    private static Proto.AttributeValue ToAttributeValue(object? value, MetadataV2FieldType? fieldType = null)
    {
        if (value is not null && fieldType is MetadataV2FieldType.Decimal or MetadataV2FieldType.TimestampOffset)
        {
            try
            {
                if (value is string text)
                {
                    if (fieldType == MetadataV2FieldType.Decimal)
                    {
                        _ = GrpcTypedValues.ExactDecimal(text);
                        return new Proto.AttributeValue { DecimalValue = text };
                    }
                    _ = GrpcTypedValues.ExactTimestampOffset(text);
                    return new Proto.AttributeValue { TimestampOffsetValue = text };
                }
            }
            catch (Exception exception) when (exception is ArgumentException or UnstorableValueException)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Stored exact value does not match its declared type."));
            }
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Stored exact value does not match its declared type."));
        }

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
                var utcDt = dt.Kind switch
                {
                    DateTimeKind.Local => dt.ToUniversalTime(),
                    DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                    _ => dt
                };
                av.DatetimeValue = new DateTimeOffset(utcDt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                break;
            case DateTimeOffset dto:
                av.DatetimeValue = dto.ToUnixTimeMilliseconds();
                break;
            case DateOnly dateOnly:
                av.StringValue = GrpcTypedValues.FormatDate(dateOnly);
                break;
            case TimeOnly timeOnly:
                av.StringValue = GrpcTypedValues.FormatTime(timeOnly);
                break;
            case Guid guid:
                av.StringValue = guid.ToString("D", CultureInfo.InvariantCulture);
                break;
            case JsonElement jsonElement:
                switch (jsonElement.ValueKind)
                {
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        av.NullValue = Proto.NullValue.NullValue;
                        break;
                    case JsonValueKind.String:
                        av.StringValue = jsonElement.GetString() ?? string.Empty;
                        break;
                    case JsonValueKind.Number:
                        if (jsonElement.TryGetInt64(out var lVal))
                        {
                            av.Int64Value = lVal;
                        }
                        else if (jsonElement.TryGetDouble(out var dVal))
                        {
                            av.DoubleValue = dVal;
                        }
                        else
                        {
                            av.StringValue = jsonElement.GetRawText();
                        }
                        break;
                    case JsonValueKind.True:
                        av.BoolValue = true;
                        break;
                    case JsonValueKind.False:
                        av.BoolValue = false;
                        break;
                    default:
                        av.StringValue = jsonElement.GetRawText();
                        break;
                }
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
            case IFormattable formattable:
                av.StringValue = formattable.ToString(null, CultureInfo.InvariantCulture);
                break;
            default:
                av.StringValue = GrpcTypedValues.ToInvariantText(value);
                break;
        }
        return av;
    }

    private static Feature ToDomainFeature(Proto.Feature feature, bool requireId)
    {
        var attributes = feature.Attributes.Count == 0
            ? ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            : feature.Attributes.ToImmutableDictionary(
                pair => pair.Key,
                pair => ToDomainAttributeValue(pair.Value),
                StringComparer.OrdinalIgnoreCase);

        var featureId = feature.Id;
        if (featureId <= 0 && requireId && TryResolveFeatureIdFromAttributes(attributes, out var attributeObjectId))
        {
            featureId = attributeObjectId;
        }

        if (requireId && featureId <= 0)
        {
            throw new ArgumentException("Update features must include a positive id (or objectId attribute).");
        }

        var geometry = feature.Geometry == null || feature.Geometry.ShapeCase == Proto.Geometry.ShapeOneofCase.None
            ? null
            : ProtoGeometryToWkb(feature.Geometry);

        return Feature.Create(featureId, geometry, attributes);
    }

    private static object? ToDomainAttributeValue(Proto.AttributeValue value)
    {
        return value.ValueCase switch
        {
            Proto.AttributeValue.ValueOneofCase.StringValue => value.StringValue,
            Proto.AttributeValue.ValueOneofCase.Int32Value => value.Int32Value,
            Proto.AttributeValue.ValueOneofCase.Int64Value => value.Int64Value,
            Proto.AttributeValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            Proto.AttributeValue.ValueOneofCase.FloatValue => value.FloatValue,
            Proto.AttributeValue.ValueOneofCase.BoolValue => value.BoolValue,
            Proto.AttributeValue.ValueOneofCase.DatetimeValue => DateTimeOffset.FromUnixTimeMilliseconds(value.DatetimeValue).UtcDateTime,
            Proto.AttributeValue.ValueOneofCase.BytesValue => value.BytesValue.ToByteArray(),
            // Validated, then stored as canonical text like every other edit protocol does: the
            // managed writer serializes attributes without DateOnly/TimeOnly converters.
            Proto.AttributeValue.ValueOneofCase.DateValue
                => GrpcTypedValues.FormatDate(GrpcTypedValues.ParseDate(value.DateValue)),
            Proto.AttributeValue.ValueOneofCase.TimeValue
                => GrpcTypedValues.FormatTime(GrpcTypedValues.ParseTime(value.TimeValue)),
            Proto.AttributeValue.ValueOneofCase.TimestampOffsetValue
                => GrpcTypedValues.ExactTimestampOffset(value.TimestampOffsetValue),
            Proto.AttributeValue.ValueOneofCase.DecimalValue => GrpcTypedValues.ExactDecimal(value.DecimalValue),
            _ => null
        };
    }

    private static bool TryResolveFeatureIdFromAttributes(
        ImmutableDictionary<string, object?> attributes,
        out long objectId)
    {
        objectId = 0;

        if (!TryGetAttributeValue(attributes, "objectid", out var raw) &&
            !TryGetAttributeValue(attributes, "id", out raw))
        {
            return false;
        }

        switch (raw)
        {
            case long l when l > 0:
                objectId = l;
                return true;
            case int i when i > 0:
                objectId = i;
                return true;
            case short s when s > 0:
                objectId = s;
                return true;
            case uint ui when ui > 0:
                objectId = ui;
                return true;
            case ulong ul when ul > 0 && ul <= long.MaxValue:
                objectId = (long)ul;
                return true;
            case string text when long.TryParse(text, out var parsed) && parsed > 0:
                objectId = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetAttributeValue(
        ImmutableDictionary<string, object?> attributes,
        string key,
        out object? value)
    {
        if (attributes.TryGetValue(key, out value))
        {
            return true;
        }

        var alternateKey = attributes.Keys.FirstOrDefault(existing =>
            existing.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (alternateKey != null && attributes.TryGetValue(alternateKey, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static Proto.EditResult ToProtoEditResult(EditOperationResult result)
    {
        var proto = new Proto.EditResult
        {
            Success = result.IsSuccess,
            ObjectId = result.ObjectId ?? 0,
            GlobalId = result.GlobalId is { Length: > 0 } globalId ? GrpcGlobalIds.Normalize(globalId) : string.Empty
        };

        if (!result.IsSuccess)
        {
            proto.Error = new Proto.ErrorDetail
            {
                Code = result.ErrorCode,
                Message = result.ErrorMessage ?? "Edit operation failed.",
                Category = Proto.ErrorCategory.Execution
            };
        }

        return proto;
    }

    internal static Proto.Geometry ToProtoGeometry(byte[] wkb, GeometryLimits? geometryLimits)
    {
        var requiresSimplification = geometryLimits is { SimplifyTolerance: > 0 };
        int? coordinatePrecision = geometryLimits is { MaxCoordinatePrecision: >= 0 }
            ? Math.Clamp(geometryLimits.MaxCoordinatePrecision, 0, 15)
            : null;

        if (!requiresSimplification && TryParseWkbToProtoGeometry(wkb, coordinatePrecision, out var directProto))
        {
            return directProto;
        }

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

            case GeometryCollection collection:
                TryProjectGeometryCollection(collection, proto);
                break;
        }

        return proto;
    }

    private const uint EwkbZFlag = 0x80000000u;
    private const uint EwkbMFlag = 0x40000000u;
    private const uint EwkbSridFlag = 0x20000000u;
    private const int MaxWkbCollectionDepth = 32;

    internal static bool TryParseWkbToProtoGeometry(
        ReadOnlySpan<byte> wkb,
        int? coordinatePrecision,
        out Proto.Geometry proto)
    {
        proto = new Proto.Geometry();
        int? clampedPrecision = coordinatePrecision.HasValue
            ? Math.Clamp(coordinatePrecision.Value, 0, 15)
            : null;
        var offset = 0;
        return TryReadWkbGeometry(wkb, ref offset, clampedPrecision, proto) && offset == wkb.Length;
    }

    private static bool TryReadWkbGeometry(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        int? precision,
        Proto.Geometry proto)
    {
        if (!TryReadWkbHeader(wkb, ref offset, out var littleEndian, out var baseType, out var hasZ, out var hasM))
        {
            return false;
        }

        switch (baseType)
        {
            case 1:
                if (!TryReadWkbPoint(wkb, ref offset, littleEndian, hasZ, hasM, precision, out var pt))
                {
                    return false;
                }

                if (pt != null)
                {
                    proto.Point = pt;
                }
                return true;

            case 2:
                if (!TryReadWkbCoordSequence(wkb, ref offset, littleEndian, hasZ, hasM, precision, out var lineSeq))
                {
                    return false;
                }

                var polyline = new Proto.PolylineGeometry();
                polyline.Paths.Add(lineSeq);
                proto.Polyline = polyline;
                return true;

            case 3:
                if (!TryReadWkbPolygonBody(wkb, ref offset, littleEndian, hasZ, hasM, precision, out var poly))
                {
                    return false;
                }

                proto.Polygon = poly;
                return true;

            case 4:
                if (!TryReadWkbMultiPoint(wkb, ref offset, littleEndian, precision, out var mp))
                {
                    return false;
                }

                proto.MultiPoint = mp;
                return true;

            case 5:
                if (!TryReadWkbMultiLineString(wkb, ref offset, littleEndian, precision, out var mlPoly))
                {
                    return false;
                }

                proto.Polyline = mlPoly;
                return true;

            case 6:
                if (!TryReadWkbMultiPolygon(wkb, ref offset, littleEndian, precision, out var mpoly))
                {
                    return false;
                }

                proto.MultiPolygon = mpoly;
                return true;

            case 7:
                return TryReadWkbGeometryCollection(wkb, ref offset, littleEndian, precision, proto);

            default:
                return false;
        }
    }

    private static bool TryReadWkbHeader(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        out bool littleEndian,
        out uint baseType,
        out bool hasZ,
        out bool hasM)
    {
        littleEndian = true;
        baseType = 0;
        hasZ = false;
        hasM = false;

        if (wkb.Length - offset < 5)
        {
            return false;
        }

        var byteOrder = wkb[offset];
        if (byteOrder is not 0 and not 1)
        {
            return false;
        }

        littleEndian = byteOrder == 1;
        var rawType = ReadUInt32(wkb.Slice(offset + 1, 4), littleEndian);
        offset += 5;

        hasZ = (rawType & EwkbZFlag) != 0;
        hasM = (rawType & EwkbMFlag) != 0;
        var hasSrid = (rawType & EwkbSridFlag) != 0;

        baseType = rawType & 0xFFFFu;
        if (baseType is >= 3000 and < 4000)
        {
            hasZ = true;
            hasM = true;
            baseType -= 3000;
        }
        else if (baseType is >= 2000 and < 3000)
        {
            hasM = true;
            baseType -= 2000;
        }
        else if (baseType is >= 1000 and < 2000)
        {
            hasZ = true;
            baseType -= 1000;
        }

        if (hasSrid)
        {
            if (wkb.Length - offset < 4)
            {
                return false;
            }

            offset += 4;
        }

        return true;
    }

    private static bool TryReadWkbPoint(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        bool hasZ,
        bool hasM,
        int? precision,
        out Proto.PointGeometry? point)
    {
        point = null;
        var coordBytes = 16 + (hasZ ? 8 : 0) + (hasM ? 8 : 0);
        if (wkb.Length - offset < coordBytes)
        {
            return false;
        }

        var x = ReadDouble(wkb.Slice(offset, 8), littleEndian);
        var y = ReadDouble(wkb.Slice(offset + 8, 8), littleEndian);
        offset += 16;

        var z = double.NaN;
        if (hasZ)
        {
            z = ReadDouble(wkb.Slice(offset, 8), littleEndian);
            offset += 8;
        }

        var m = double.NaN;
        if (hasM)
        {
            m = ReadDouble(wkb.Slice(offset, 8), littleEndian);
            offset += 8;
        }

        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return true;
        }

        var pt = new Proto.PointGeometry
        {
            X = RoundOrdinate(x, precision),
            Y = RoundOrdinate(y, precision)
        };
        if (hasZ && !double.IsNaN(z))
        {
            pt.Z = RoundOrdinate(z, precision);
        }

        if (hasM && !double.IsNaN(m))
        {
            pt.M = RoundOrdinate(m, precision);
        }

        point = pt;
        return true;
    }

    private static bool TryReadWkbCoordSequence(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        bool hasZ,
        bool hasM,
        int? precision,
        out Proto.CoordinateSequence sequence)
    {
        sequence = null!;
        if (!TryReadWkbCount(wkb, ref offset, littleEndian, out var count))
        {
            return false;
        }

        var coordBytes = 16 + (hasZ ? 8 : 0) + (hasM ? 8 : 0);
        if ((long)count * coordBytes > wkb.Length - offset)
        {
            return false;
        }

        sequence = new Proto.CoordinateSequence { Coords = { Capacity = count } };
        for (var i = 0; i < count; i++)
        {
            var x = ReadDouble(wkb.Slice(offset, 8), littleEndian);
            var y = ReadDouble(wkb.Slice(offset + 8, 8), littleEndian);
            offset += 16;

            var c = new Proto.Coordinate
            {
                X = RoundOrdinate(x, precision),
                Y = RoundOrdinate(y, precision)
            };

            if (hasZ)
            {
                var z = ReadDouble(wkb.Slice(offset, 8), littleEndian);
                offset += 8;
                if (!double.IsNaN(z))
                {
                    c.Z = RoundOrdinate(z, precision);
                }
            }

            if (hasM)
            {
                var m = ReadDouble(wkb.Slice(offset, 8), littleEndian);
                offset += 8;
                if (!double.IsNaN(m))
                {
                    c.M = RoundOrdinate(m, precision);
                }
            }

            sequence.Coords.Add(c);
        }

        return true;
    }

    private static bool TryReadWkbPolygonBody(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        bool hasZ,
        bool hasM,
        int? precision,
        out Proto.PolygonGeometry polygon)
    {
        polygon = null!;
        if (!TryReadWkbCount(wkb, ref offset, littleEndian, out var ringCount))
        {
            return false;
        }

        if ((long)ringCount * 4 > wkb.Length - offset)
        {
            return false;
        }

        polygon = new Proto.PolygonGeometry();
        if (ringCount == 0)
        {
            polygon.Rings.Add(new Proto.CoordinateSequence());
            return true;
        }

        for (var i = 0; i < ringCount; i++)
        {
            if (!TryReadWkbCoordSequence(wkb, ref offset, littleEndian, hasZ, hasM, precision, out var ring))
            {
                return false;
            }

            polygon.Rings.Add(ring);
        }

        return true;
    }

    private static bool TryReadWkbMultiPoint(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        int? precision,
        out Proto.MultiPointGeometry multiPoint)
    {
        multiPoint = null!;
        if (!TryReadWkbCount(wkb, ref offset, littleEndian, out var count))
        {
            return false;
        }

        if ((long)count * 21 > wkb.Length - offset)
        {
            return false;
        }

        multiPoint = new Proto.MultiPointGeometry();
        for (var i = 0; i < count; i++)
        {
            if (!TryReadWkbHeader(wkb, ref offset, out var childLe, out var childType, out var childZ, out var childM)
                || childType != 1
                || !TryReadWkbPoint(wkb, ref offset, childLe, childZ, childM, precision, out var pt))
            {
                return false;
            }

            if (pt != null)
            {
                multiPoint.Points.Add(pt);
            }
        }

        return true;
    }

    private static bool TryReadWkbMultiLineString(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        int? precision,
        out Proto.PolylineGeometry polyline)
    {
        polyline = null!;
        if (!TryReadWkbCount(wkb, ref offset, littleEndian, out var count))
        {
            return false;
        }

        if ((long)count * 9 > wkb.Length - offset)
        {
            return false;
        }

        polyline = new Proto.PolylineGeometry();
        for (var i = 0; i < count; i++)
        {
            if (!TryReadWkbHeader(wkb, ref offset, out var childLe, out var childType, out var childZ, out var childM)
                || childType != 2
                || !TryReadWkbCoordSequence(wkb, ref offset, childLe, childZ, childM, precision, out var seq))
            {
                return false;
            }

            polyline.Paths.Add(seq);
        }

        return true;
    }

    private static bool TryReadWkbMultiPolygon(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        int? precision,
        out Proto.MultiPolygonGeometry multiPolygon)
    {
        multiPolygon = null!;
        if (!TryReadWkbCount(wkb, ref offset, littleEndian, out var count))
        {
            return false;
        }

        if ((long)count * 9 > wkb.Length - offset)
        {
            return false;
        }

        multiPolygon = new Proto.MultiPolygonGeometry();
        for (var i = 0; i < count; i++)
        {
            if (!TryReadWkbHeader(wkb, ref offset, out var childLe, out var childType, out var childZ, out var childM)
                || childType != 3
                || !TryReadWkbPolygonBody(wkb, ref offset, childLe, childZ, childM, precision, out var poly))
            {
                return false;
            }

            multiPolygon.Polygons.Add(poly);
        }

        return true;
    }

    private static bool TryReadWkbGeometryCollection(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        int? precision,
        Proto.Geometry proto)
    {
        List<Proto.PointGeometry>? points = null;
        List<Proto.CoordinateSequence>? paths = null;
        List<Proto.PolygonGeometry>? polygons = null;

        if (!TryFlattenWkbCollectionBody(
            wkb,
            ref offset,
            littleEndian,
            precision,
            depth: 0,
            ref points,
            ref paths,
            ref polygons))
        {
            return false;
        }

        var hasPoints = points is { Count: > 0 };
        var hasPaths = paths is { Count: > 0 };
        var hasPolygons = polygons is { Count: > 0 };
        var distinctKinds = (hasPoints ? 1 : 0) + (hasPaths ? 1 : 0) + (hasPolygons ? 1 : 0);
        if (distinctKinds != 1)
        {
            return true;
        }

        if (hasPoints)
        {
            var mp = new Proto.MultiPointGeometry();
            mp.Points.AddRange(points!);
            proto.MultiPoint = mp;
        }
        else if (hasPaths)
        {
            var pl = new Proto.PolylineGeometry();
            pl.Paths.AddRange(paths!);
            proto.Polyline = pl;
        }
        else if (hasPolygons)
        {
            var mpoly = new Proto.MultiPolygonGeometry();
            mpoly.Polygons.AddRange(polygons!);
            proto.MultiPolygon = mpoly;
        }

        return true;
    }

    private static bool TryFlattenWkbCollectionBody(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        int? precision,
        int depth,
        ref List<Proto.PointGeometry>? points,
        ref List<Proto.CoordinateSequence>? paths,
        ref List<Proto.PolygonGeometry>? polygons)
    {
        if (depth > MaxWkbCollectionDepth || !TryReadWkbCount(wkb, ref offset, littleEndian, out var count))
        {
            return false;
        }

        if ((long)count * 5 > wkb.Length - offset)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!TryReadWkbHeader(wkb, ref offset, out var childLe, out var childType, out var childZ, out var childM))
            {
                return false;
            }

            switch (childType)
            {
                case 1:
                    if (!TryReadWkbPoint(wkb, ref offset, childLe, childZ, childM, precision, out var pt))
                    {
                        return false;
                    }

                    if (pt != null)
                    {
                        (points ??= []).Add(pt);
                    }
                    break;

                case 2:
                    if (!TryReadWkbCoordSequence(wkb, ref offset, childLe, childZ, childM, precision, out var seq))
                    {
                        return false;
                    }

                    (paths ??= []).Add(seq);
                    break;

                case 3:
                    if (!TryReadWkbPolygonBody(wkb, ref offset, childLe, childZ, childM, precision, out var poly))
                    {
                        return false;
                    }

                    (polygons ??= []).Add(poly);
                    break;

                case 4:
                    if (!TryReadWkbMultiPoint(wkb, ref offset, childLe, precision, out var mp))
                    {
                        return false;
                    }

                    (points ??= []).AddRange(mp.Points);
                    break;

                case 5:
                    if (!TryReadWkbMultiLineString(wkb, ref offset, childLe, precision, out var ml))
                    {
                        return false;
                    }

                    (paths ??= []).AddRange(ml.Paths);
                    break;

                case 6:
                    if (!TryReadWkbMultiPolygon(wkb, ref offset, childLe, precision, out var mpoly))
                    {
                        return false;
                    }

                    (polygons ??= []).AddRange(mpoly.Polygons);
                    break;

                case 7:
                    if (!TryFlattenWkbCollectionBody(
                        wkb,
                        ref offset,
                        childLe,
                        precision,
                        depth + 1,
                        ref points,
                        ref paths,
                        ref polygons))
                    {
                        return false;
                    }
                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadWkbCount(
        ReadOnlySpan<byte> wkb,
        ref int offset,
        bool littleEndian,
        out int count)
    {
        count = 0;
        if (wkb.Length - offset < 4)
        {
            return false;
        }

        var raw = ReadUInt32(wkb.Slice(offset, 4), littleEndian);
        if (raw > int.MaxValue)
        {
            return false;
        }

        offset += 4;
        count = (int)raw;
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(span)
            : BinaryPrimitives.ReadUInt32BigEndian(span);

    private static double ReadDouble(ReadOnlySpan<byte> span, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadDoubleLittleEndian(span)
            : BinaryPrimitives.ReadDoubleBigEndian(span);

    private static double RoundOrdinate(double value, int? precision)
        => precision.HasValue
            ? Math.Round(value, precision.Value, MidpointRounding.AwayFromZero)
            : value;

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

    private static Proto.FieldType ToProtoFieldType(MetadataV2FieldType fieldType)
    {
        return fieldType switch
        {
            MetadataV2FieldType.String => Proto.FieldType.String,
            MetadataV2FieldType.Integer => Proto.FieldType.Integer,
            MetadataV2FieldType.BigInteger => Proto.FieldType.BigInteger,
            MetadataV2FieldType.Double => Proto.FieldType.Double,
            MetadataV2FieldType.Float => Proto.FieldType.Float,
            MetadataV2FieldType.Boolean => Proto.FieldType.Boolean,
            MetadataV2FieldType.DateTime => Proto.FieldType.DateTime,
            MetadataV2FieldType.Date => Proto.FieldType.Date,
            MetadataV2FieldType.Time => Proto.FieldType.Time,
            MetadataV2FieldType.Decimal => Proto.FieldType.Decimal,
            MetadataV2FieldType.TimestampOffset => Proto.FieldType.TimestampOffset,
            MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography => Proto.FieldType.Geometry,
            MetadataV2FieldType.Json => Proto.FieldType.Json,
            MetadataV2FieldType.Binary => Proto.FieldType.Binary,
            MetadataV2FieldType.Uuid => Proto.FieldType.Uuid,
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
            Srid = ResolveSpatialFilterSrid(filter.SpatialReference),
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
            Proto.Geometry.ShapeOneofCase.Point => _geoFactory.CreatePoint(ToCoordinate(geometry.Point)),
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

        var shell = _geoFactory.CreateLinearRing(
            poly.Rings[0].Coords.Select(ToCoordinate).ToArray());
        var holes = poly.Rings.Skip(1)
            .Select(r => _geoFactory.CreateLinearRing(
                r.Coords.Select(ToCoordinate).ToArray()))
            .ToArray();

        return _geoFactory.CreatePolygon(shell, holes);
    }

    private static MultiLineString BuildPolyline(Proto.PolylineGeometry polyline)
    {
        var lines = polyline.Paths.Select(p =>
            _geoFactory.CreateLineString(p.Coords.Select(ToCoordinate).ToArray()))
            .ToArray();
        return _geoFactory.CreateMultiLineString(lines);
    }

    private static MultiPoint BuildMultiPoint(Proto.MultiPointGeometry mp)
    {
        var points = mp.Points.Select(p => _geoFactory.CreatePoint(ToCoordinate(p))).ToArray();
        return _geoFactory.CreateMultiPoint(points);
    }

    private static MultiPolygon BuildMultiPolygon(Proto.MultiPolygonGeometry multiPoly)
    {
        var polygons = multiPoly.Polygons.Select(BuildPolygon).ToArray();
        return _geoFactory.CreateMultiPolygon(polygons);
    }

    private static Coordinate ToCoordinate(Proto.PointGeometry point)
    {
        return CreateCoordinate(point.X, point.Y, point.HasZ, point.Z, point.HasM, point.M);
    }

    private static Coordinate ToCoordinate(Proto.Coordinate coordinateValue)
    {
        return CreateCoordinate(
            coordinateValue.X,
            coordinateValue.Y,
            coordinateValue.HasZ,
            coordinateValue.Z,
            coordinateValue.HasM,
            coordinateValue.M);
    }

    private static Coordinate CreateCoordinate(
        double x,
        double y,
        bool hasZ,
        double z,
        bool hasM,
        double m)
    {
        if (hasZ && hasM)
        {
            return new CoordinateZM(x, y, z, m);
        }

        if (hasZ)
        {
            return new CoordinateZ(x, y, z);
        }

        if (hasM)
        {
            return new CoordinateM(x, y, m);
        }

        return new Coordinate(x, y);
    }

    private static int? ResolveSpatialFilterSrid(Proto.SpatialReference? spatialReference)
        => TryResolveOutputSrid(spatialReference);

    private static void TryProjectGeometryCollection(GeometryCollection collection, Proto.Geometry proto)
    {
        var flattened = FlattenGeometryCollection(collection).ToArray();
        if (flattened.Length == 0)
        {
            return;
        }

        if (flattened.All(static g => g is Point))
        {
            var multiPoint = new Proto.MultiPointGeometry();
            foreach (var point in flattened.Cast<Point>())
            {
                var protoPoint = new Proto.PointGeometry { X = point.X, Y = point.Y };
                if (!double.IsNaN(point.Z))
                {
                    protoPoint.Z = point.Z;
                }

                if (!double.IsNaN(point.M))
                {
                    protoPoint.M = point.M;
                }

                multiPoint.Points.Add(protoPoint);
            }

            proto.MultiPoint = multiPoint;
            return;
        }

        if (flattened.All(static g => g is LineString))
        {
            var polyline = new Proto.PolylineGeometry();
            foreach (var lineString in flattened.Cast<LineString>())
            {
                polyline.Paths.Add(ToCoordinateSequence(lineString.CoordinateSequence));
            }

            proto.Polyline = polyline;
            return;
        }

        if (flattened.All(static g => g is Polygon))
        {
            var multiPolygon = new Proto.MultiPolygonGeometry();
            foreach (var polygon in flattened.Cast<Polygon>())
            {
                var polygonPart = new Proto.PolygonGeometry();
                polygonPart.Rings.Add(ToCoordinateSequence(polygon.ExteriorRing.CoordinateSequence));
                foreach (var hole in polygon.InteriorRings)
                {
                    polygonPart.Rings.Add(ToCoordinateSequence(hole.CoordinateSequence));
                }

                multiPolygon.Polygons.Add(polygonPart);
            }

            proto.MultiPolygon = multiPolygon;
        }
    }

    private static IEnumerable<Geometry> FlattenGeometryCollection(Geometry geometry)
    {
        switch (geometry)
        {
            case Point or LineString or Polygon:
                yield return geometry;
                yield break;
            case MultiPoint multiPoint:
                foreach (var point in multiPoint.Geometries)
                {
                    yield return point;
                }

                yield break;
            case MultiLineString multiLineString:
                foreach (var lineString in multiLineString.Geometries)
                {
                    yield return lineString;
                }

                yield break;
            case MultiPolygon multiPolygon:
                foreach (var polygon in multiPolygon.Geometries)
                {
                    yield return polygon;
                }

                yield break;
            case GeometryCollection nestedCollection:
                foreach (var child in nestedCollection.Geometries.SelectMany(FlattenGeometryCollection))
                {
                    yield return child;
                }

                yield break;
        }
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
}
