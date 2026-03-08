// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.FeatureServer.Services;

/// <summary>
/// Formats query results as Esri-compatible Protocol Buffer (PBF) responses.
/// Implements the FeatureCollectionPBuffer schema from github.com/Esri/arcgis-pbf.
/// </summary>
/// <remarks>
/// Key encoding details:
/// - Geometry coordinates are delta-encoded signed integers
/// - A Transform message carries scale/translate for world coordinate recovery
/// - Attribute values use interned field indices for compact storage
/// - The wire format uses hand-rolled protobuf encoding (no library dependency)
/// </remarks>
internal sealed class PbfQueryFormatter
{
    // Esri PBF schema version
    private const string PbfVersion = "1.0";

    // Default quantization precision: 6 decimal places (~0.1m at equator)
    private const double DefaultQuantizationScale = 1e6;

    private readonly GeometryLimits _geometryLimits;

    [ThreadStatic]
    private static WKBReader? _wkbReader;

    /// <summary>
    /// Initializes the PBF formatter with geometry processing limits.
    /// </summary>
    public PbfQueryFormatter(IOptions<LimitsOptions> limitsOptions)
    {
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
    }

    /// <summary>
    /// Formats a query result as an Esri FeatureCollectionPBuffer.
    /// </summary>
    /// <returns>A tuple of the PBF byte array and content type.</returns>
    public (byte[] response, string contentType) FormatAsPbf(
        QueryResult<Feature> result,
        LayerDefinition layer,
        bool returnGeometry,
        int? outputSrid,
        bool returnZ,
        bool returnM,
        int? geometryPrecision,
        double? maxAllowableOffset,
        string[]? outFields)
    {
        var effectiveLimits = GeometryOutputProcessor.CreateEffectiveLimits(
            _geometryLimits,
            geometryPrecision,
            maxAllowableOffset,
            forceSimplify: maxAllowableOffset is > 0);

        var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;
        var srid = outputSrid ?? layer.SpatialReference.Wkid;

        // Build the FeatureResult sub-message
        var featureResult = new ProtobufWriter(result.Items.Length * 256);
        try
        {
            WriteFeatureResult(
                ref featureResult,
                result,
                layer,
                objectIdFieldName,
                srid,
                returnGeometry,
                returnZ,
                returnM,
                effectiveLimits,
                outFields);

            // Wrap in QueryResult → FeatureCollectionPBuffer
            var queryResult = new ProtobufWriter(featureResult.Position + 16);
            try
            {
                // QueryResult.featureResult = field 1
                queryResult.WriteMessage(1, ref featureResult);

                var outer = new ProtobufWriter(queryResult.Position + 64);
                try
                {
                    // FeatureCollectionPBuffer.version = field 1
                    outer.WriteString(1, PbfVersion);
                    // FeatureCollectionPBuffer.queryResult = field 2
                    outer.WriteMessage(2, ref queryResult);

                    return (outer.ToArrayAndDispose(), "application/x-protobuf");
                }
                catch
                {
                    outer.Dispose();
                    throw;
                }
            }
            finally
            {
                queryResult.Dispose();
            }
        }
        finally
        {
            featureResult.Dispose();
        }
    }

    /// <summary>
    /// Writes a FeatureResult message containing fields, features, and metadata.
    /// </summary>
    private static void WriteFeatureResult(
        ref ProtobufWriter writer,
        QueryResult<Feature> result,
        LayerDefinition layer,
        string objectIdFieldName,
        int srid,
        bool returnGeometry,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields)
    {
        // field 1: objectIdFieldName
        writer.WriteString(1, objectIdFieldName);

        // field 7: geometryType
        if (layer.HasGeometry)
        {
            writer.WriteEnum(7, MapPbfGeometryType(layer.GeometryType));
        }

        // field 8: spatialReference
        if (layer.HasGeometry)
        {
            var sr = new ProtobufWriter(32);
            sr.WriteUInt32(1, (uint)srid);       // wkid
            sr.WriteUInt32(2, (uint)srid);       // latestWkid
            writer.WriteMessage(8, ref sr);
            sr.Dispose();
        }

        // field 9: exceededTransferLimit
        writer.WriteBool(9, result.HasMoreResults);

        // field 10: hasZ
        writer.WriteBool(10, returnZ);

        // field 11: hasM
        writer.WriteBool(11, returnM);

        // field 12: transform (quantization parameters)
        if (layer.HasGeometry && returnGeometry)
        {
            WriteTransform(ref writer, srid);
        }

        // field 13: fields (repeated)
        var queryFields = QueryFormatter.BuildQueryFields(layer, outFields, objectIdFieldName);
        foreach (var field in queryFields)
        {
            var fieldMsg = new ProtobufWriter(64);
            fieldMsg.WriteString(1, field.Name);                      // name
            fieldMsg.WriteEnum(2, MapPbfFieldType(field.Type));       // fieldType
            fieldMsg.WriteString(3, field.Alias ?? field.Name);       // alias
            writer.WriteMessage(13, ref fieldMsg);
            fieldMsg.Dispose();
        }

        // Build field name → index map for attribute value encoding
        var fieldIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < queryFields.Length; i++)
        {
            fieldIndex[queryFields[i].Name] = i;
        }

        // field 15: features (repeated)
        double scale = ComputeScale(geometryLimits);
        foreach (var feature in result.Items)
        {
            var featureMsg = new ProtobufWriter(256);
            WriteFeature(
                ref featureMsg,
                feature,
                queryFields,
                fieldIndex,
                objectIdFieldName,
                returnGeometry,
                returnZ,
                returnM,
                geometryLimits,
                scale);
            writer.WriteMessage(15, ref featureMsg);
            featureMsg.Dispose();
        }
    }

    /// <summary>
    /// Writes a single Feature message with attributes and geometry.
    /// </summary>
    private static void WriteFeature(
        ref ProtobufWriter writer,
        Feature feature,
        GeoServicesFieldInfo[] fields,
        Dictionary<string, int> fieldIndex,
        string objectIdFieldName,
        bool returnGeometry,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        double scale)
    {
        // field 1: attributes (repeated Value messages)
        WriteAttributes(ref writer, feature, fields, fieldIndex, objectIdFieldName);

        // field 2: geometry
        if (returnGeometry && feature.Geometry != null)
        {
            WriteGeometry(ref writer, feature.Geometry, returnZ, returnM, geometryLimits, scale);
        }
    }

    /// <summary>
    /// Writes attribute values as repeated Value messages (field 1 on Feature).
    /// </summary>
    private static void WriteAttributes(
        ref ProtobufWriter writer,
        Feature feature,
        GeoServicesFieldInfo[] fields,
        Dictionary<string, int> fieldIndex,
        string objectIdFieldName)
    {
        foreach (var field in fields)
        {
            object? value;
            if (field.Name.Equals(objectIdFieldName, StringComparison.OrdinalIgnoreCase) &&
                !feature.Attributes.TryGetValue(field.Name, out value))
            {
                value = feature.Id;
            }
            else
            {
                feature.Attributes.TryGetValue(field.Name, out value);
            }

            var valueMsg = new ProtobufWriter(32);

            if (value == null)
            {
                // null_value = field 10 (bool, true = null)
                valueMsg.WriteBool(10, true);
            }
            else
            {
                WriteAttributeValue(ref valueMsg, value);
            }

            // index = field 11 (optional uint32)
            if (fieldIndex.TryGetValue(field.Name, out int idx))
            {
                // WriteUInt32 skips zero, but field index 0 is valid;
                // use WriteTag + WriteRawVarint directly
                valueMsg.WriteTag(11, 0);
                valueMsg.WriteRawVarint((uint)idx);
            }

            writer.WriteMessage(1, ref valueMsg);
            valueMsg.Dispose();
        }
    }

    /// <summary>
    /// Writes a single attribute value into the Value oneof.
    /// </summary>
    private static void WriteAttributeValue(ref ProtobufWriter writer, object value)
    {
        switch (value)
        {
            case string s:
                writer.WriteString(1, s);       // string_value
                break;
            case float f:
                writer.WriteFloat(2, f);        // float_value
                break;
            case double d:
                writer.WriteDouble(3, d);       // double_value
                break;
            case int i:
                writer.WriteSInt32(4, i);       // sint_value
                break;
            case uint u:
                writer.WriteUInt32(5, u);       // uint_value
                break;
            case long l:
                writer.WriteInt64(6, l);        // int64_value
                break;
            case ulong ul:
                writer.WriteUInt64(7, ul);      // uint64_value
                break;
            case bool b:
                writer.WriteBool(9, b);         // bool_value
                break;
            case short s:
                writer.WriteSInt32(4, s);
                break;
            case decimal d:
                writer.WriteDouble(3, (double)d);
                break;
            case DateTime dt:
                // Esri PBF encodes dates as int64 milliseconds since epoch
                writer.WriteInt64(6, new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds());
                break;
            case DateTimeOffset dto:
                writer.WriteInt64(6, dto.ToUnixTimeMilliseconds());
                break;
            case Guid g:
                writer.WriteString(1, g.ToString());
                break;
            default:
                writer.WriteString(1, value.ToString());
                break;
        }
    }

    /// <summary>
    /// Writes a Geometry message with delta-encoded integer coordinates.
    /// </summary>
    private static void WriteGeometry(
        ref ProtobufWriter writer,
        byte[] wkb,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        double scale)
    {
        _wkbReader ??= new WKBReader();
        Geometry? geometry;
        try
        {
            geometry = _wkbReader.Read(wkb);
        }
        catch
        {
            return; // Skip unparseable geometry
        }

        if (geometry == null || geometry.IsEmpty)
            return;

        var geoMsg = new ProtobufWriter(geometry.NumPoints * 16 + 32);

        // field 1: geometryType
        geoMsg.WriteEnum(1, MapNtsGeometryType(geometry));

        // Encode coordinates as delta-encoded sint64 values
        var (lengths, coords) = EncodeGeometryCoordinates(geometry, scale, returnZ, returnM);

        // field 2: lengths (packed uint32)
        if (lengths.Count > 0)
        {
            geoMsg.WritePackedUInt32(2, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lengths));
        }

        // field 3: coords (packed sint64)
        if (coords.Count > 0)
        {
            geoMsg.WritePackedSInt64(3, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(coords));
        }

        writer.WriteMessage(2, ref geoMsg);
        geoMsg.Dispose();
    }

    /// <summary>
    /// Delta-encodes geometry coordinates into integer arrays.
    /// </summary>
    private static (List<uint> lengths, List<long> coords) EncodeGeometryCoordinates(
        Geometry geometry,
        double scale,
        bool returnZ,
        bool returnM)
    {
        var lengths = new List<uint>();
        var coords = new List<long>();
        long prevX = 0, prevY = 0;

        switch (geometry)
        {
            case Point pt:
                AppendCoordinate(coords, pt.Coordinate, scale, ref prevX, ref prevY, returnZ, returnM);
                break;

            case MultiPoint mp:
                lengths.Add((uint)mp.NumGeometries);
                for (int i = 0; i < mp.NumGeometries; i++)
                {
                    AppendCoordinate(coords, mp.GetGeometryN(i).Coordinate, scale, ref prevX, ref prevY, returnZ, returnM);
                }
                break;

            case LineString ls:
                AppendCoordinateSequence(coords, ls.CoordinateSequence, scale, ref prevX, ref prevY, returnZ, returnM);
                lengths.Add((uint)ls.NumPoints);
                break;

            case MultiLineString mls:
                for (int i = 0; i < mls.NumGeometries; i++)
                {
                    var line = (LineString)mls.GetGeometryN(i);
                    lengths.Add((uint)line.NumPoints);
                    AppendCoordinateSequence(coords, line.CoordinateSequence, scale, ref prevX, ref prevY, returnZ, returnM);
                }
                break;

            case Polygon poly:
                EncodePolygon(poly, coords, lengths, scale, ref prevX, ref prevY, returnZ, returnM);
                break;

            case MultiPolygon mpoly:
                for (int i = 0; i < mpoly.NumGeometries; i++)
                {
                    EncodePolygon((Polygon)mpoly.GetGeometryN(i), coords, lengths, scale, ref prevX, ref prevY, returnZ, returnM);
                }
                break;
        }

        return (lengths, coords);
    }

    private static void EncodePolygon(
        Polygon polygon,
        List<long> coords,
        List<uint> lengths,
        double scale,
        ref long prevX,
        ref long prevY,
        bool returnZ,
        bool returnM)
    {
        // Exterior ring
        var exterior = polygon.ExteriorRing;
        lengths.Add((uint)exterior.NumPoints);
        AppendCoordinateSequence(coords, exterior.CoordinateSequence, scale, ref prevX, ref prevY, returnZ, returnM);

        // Interior rings (holes)
        for (int i = 0; i < polygon.NumInteriorRings; i++)
        {
            var ring = polygon.GetInteriorRingN(i);
            lengths.Add((uint)ring.NumPoints);
            AppendCoordinateSequence(coords, ring.CoordinateSequence, scale, ref prevX, ref prevY, returnZ, returnM);
        }
    }

    private static void AppendCoordinate(
        List<long> coords,
        Coordinate coord,
        double scale,
        ref long prevX,
        ref long prevY,
        bool returnZ,
        bool returnM)
    {
        long x = (long)Math.Round(coord.X * scale);
        long y = (long)Math.Round(coord.Y * scale);
        coords.Add(x - prevX);
        coords.Add(y - prevY);
        prevX = x;
        prevY = y;

        if (returnZ && !double.IsNaN(coord.Z))
        {
            coords.Add((long)Math.Round(coord.Z * scale));
        }

        if (returnM)
        {
            var m = double.IsNaN(coord.M) ? 0d : coord.M;
            coords.Add((long)Math.Round(m * scale));
        }
    }

    private static void AppendCoordinateSequence(
        List<long> coords,
        CoordinateSequence sequence,
        double scale,
        ref long prevX,
        ref long prevY,
        bool returnZ,
        bool returnM)
    {
        for (int i = 0; i < sequence.Count; i++)
        {
            long x = (long)Math.Round(sequence.GetX(i) * scale);
            long y = (long)Math.Round(sequence.GetY(i) * scale);
            coords.Add(x - prevX);
            coords.Add(y - prevY);
            prevX = x;
            prevY = y;

            if (returnZ && sequence.HasZ)
            {
                coords.Add((long)Math.Round(sequence.GetZ(i) * scale));
            }

            if (returnM)
            {
                var m = sequence.HasM ? sequence.GetOrdinate(i, Ordinate.M) : double.NaN;
                if (double.IsNaN(m))
                {
                    m = 0d;
                }

                coords.Add((long)Math.Round(m * scale));
            }
        }
    }

    /// <summary>
    /// Writes the Transform message with quantization scale/translate.
    /// </summary>
    private static void WriteTransform(ref ProtobufWriter writer, int srid)
    {
        double scale = DefaultQuantizationScale;
        var transform = new ProtobufWriter(64);

        // field 1: quantizeOriginPosition (0 = upperLeft, default)

        // field 2: scale
        var scaleMsg = new ProtobufWriter(32);
        scaleMsg.WriteDouble(1, 1.0 / scale);   // xScale
        scaleMsg.WriteDouble(2, 1.0 / scale);   // yScale
        transform.WriteMessage(2, ref scaleMsg);
        scaleMsg.Dispose();

        // field 3: translate
        var translateMsg = new ProtobufWriter(32);
        translateMsg.WriteDouble(1, 0.0);        // xTranslate
        translateMsg.WriteDouble(2, 0.0);        // yTranslate
        transform.WriteMessage(3, ref translateMsg);
        translateMsg.Dispose();

        writer.WriteMessage(12, ref transform);
        transform.Dispose();
    }

    private static double ComputeScale(GeometryLimits limits)
    {
        return DefaultQuantizationScale;
    }

    // ── Enum mapping ──────────────────────────────────────────

    /// <summary>
    /// Maps Honua GeometryType to Esri PBF GeometryType enum values.
    /// </summary>
    private static int MapPbfGeometryType(Honua.Core.Features.Catalog.Domain.GeometryType geometryType)
    {
        return geometryType switch
        {
            Honua.Core.Features.Catalog.Domain.GeometryType.Point => 0,          // esriGeometryTypePoint
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint => 1,     // esriGeometryTypeMultipoint
            Honua.Core.Features.Catalog.Domain.GeometryType.LineString => 2,     // esriGeometryTypePolyline
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString => 2,// esriGeometryTypePolyline
            Honua.Core.Features.Catalog.Domain.GeometryType.Polygon => 3,        // esriGeometryTypePolygon
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon => 3,   // esriGeometryTypePolygon
            _ => 127                                                              // esriGeometryTypeNone
        };
    }

    /// <summary>
    /// Maps NTS Geometry to Esri PBF GeometryType enum values.
    /// </summary>
    private static int MapNtsGeometryType(Geometry geometry)
    {
        return geometry switch
        {
            Point => 0,
            MultiPoint => 1,
            LineString => 2,
            MultiLineString => 2,
            Polygon => 3,
            MultiPolygon => 3,
            _ => 127
        };
    }

    /// <summary>
    /// Maps GeoServices field type string to Esri PBF FieldType enum value.
    /// </summary>
    private static int MapPbfFieldType(string? geoServicesType)
    {
        return geoServicesType switch
        {
            "esriFieldTypeSmallInteger" => 0,
            "esriFieldTypeInteger" => 1,
            "esriFieldTypeSingle" => 2,
            "esriFieldTypeDouble" => 3,
            "esriFieldTypeString" => 4,
            "esriFieldTypeDate" => 5,
            "esriFieldTypeOID" => 6,
            "esriFieldTypeGeometry" => 7,
            "esriFieldTypeBlob" => 8,
            "esriFieldTypeGUID" => 10,
            "esriFieldTypeGlobalID" => 11,
            "esriFieldTypeXML" => 12,
            "esriFieldTypeBigInteger" => 13,
            "esriFieldTypeDateOnly" => 14,
            "esriFieldTypeTimeOnly" => 15,
            "esriFieldTypeTimestampOffset" => 16,
            _ => 4 // Default to string
        };
    }
}
