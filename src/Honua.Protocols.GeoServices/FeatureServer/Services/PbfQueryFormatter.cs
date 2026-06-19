// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

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

    /// <summary>
    /// Initializes the PBF formatter with geometry processing limits.
    /// </summary>
    public PbfQueryFormatter(IOptions<LimitsOptions> limitsOptions)
    {
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
    }

    /// <summary>
    /// Formats a <c>returnCountOnly=true</c> result as an Esri-compatible PBF response
    /// (<c>FeatureCollectionPBuffer</c> with the <c>countResult</c> arm of the
    /// <c>QueryResult</c> oneof), mirroring how the feature path emits protobuf so that
    /// <c>f=pbf</c> on the count path returns <c>application/x-protobuf</c> rather than JSON.
    /// </summary>
    public static (byte[] response, string contentType) FormatCountAsPbf(long count)
    {
        // Schema (github.com/Esri/arcgis-pbf FeatureCollection.proto):
        //   FeatureCollectionPBuffer { string version = 1; QueryResult queryResult = 2; }
        //   QueryResult { oneof Results { ... CountResult countResult = 2; ... } }
        //   CountResult { uint64 count = 1; }
        var countMsg = new ProtobufWriter(16);
        try
        {
            // count is the selected oneof arm, so emit it unconditionally (including 0)
            // to keep the CountResult message present on the wire.
            countMsg.WriteUInt64Always(1, (ulong)Math.Max(count, 0));

            var queryResult = new ProtobufWriter(countMsg.Position + 8);
            try
            {
                queryResult.WriteMessage(2, ref countMsg);

                var outer = new ProtobufWriter(queryResult.Position + 16);
                try
                {
                    outer.WriteString(1, PbfVersion);
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
            countMsg.Dispose();
        }
    }

    public (byte[] response, string contentType) FormatAsPbf(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
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

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(resource);
        var srid = outputSrid ?? resource.ReadSrid() ?? 4326;

        var featureResult = new ProtobufWriter(result.Items.Length * 256);
        try
        {
            WriteFeatureResult(
                ref featureResult,
                result,
                resource,
                objectIdFieldName,
                srid,
                returnGeometry,
                returnZ,
                returnM,
                effectiveLimits,
                outFields);

            var queryResult = new ProtobufWriter(featureResult.Position + 16);
            try
            {
                queryResult.WriteMessage(1, ref featureResult);

                var outer = new ProtobufWriter(queryResult.Position + 64);
                try
                {
                    outer.WriteString(1, PbfVersion);
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

    private static void WriteFeatureResult(
        ref ProtobufWriter writer,
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        string objectIdFieldName,
        int srid,
        bool returnGeometry,
        bool returnZ,
        bool returnM,
        GeometryLimits geometryLimits,
        string[]? outFields)
    {
        writer.WriteString(1, objectIdFieldName);

        var geometryType = resource.ReadGeometryType();
        var hasGeometry = geometryType != MetadataV2GeometryType.None || resource.FindPrimaryGeometryField() is not null;
        if (hasGeometry)
        {
            writer.WriteEnum(7, MapPbfGeometryType(geometryType));
        }

        if (hasGeometry)
        {
            var sr = new ProtobufWriter(32);
            sr.WriteUInt32(1, (uint)srid);
            sr.WriteUInt32(2, (uint)srid);
            writer.WriteMessage(8, ref sr);
            sr.Dispose();
        }

        // hasZ/hasM (fields 10/11) must describe the coordinate stride actually
        // emitted, not the request parameters: decoders read 2/3/4 ordinates per
        // vertex based on these flags, so advertising Z for a 2D layer queried with
        // returnZ=true would garble every subsequent vertex. Derive the effective
        // flags from the data (mirroring the f=json path) and emit a Z/M ordinate
        // for EVERY vertex whenever the corresponding flag is set.
        var (dataHasZ, dataHasM) = DetectZmOrdinates(result.Items);
        var effectiveZ = returnZ && dataHasZ;
        var effectiveM = returnM && dataHasM;

        writer.WriteBool(9, result.HasMoreResults);
        writer.WriteBool(10, effectiveZ);
        writer.WriteBool(11, effectiveM);

        if (hasGeometry && returnGeometry)
        {
            WriteTransform(ref writer, effectiveZ, effectiveM);
        }

        var declaredAttributeFields = resource.SchemaFields
            .Where(static field => field.Type is not (MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography))
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeFields = QueryFormatter.DetectRuntimeFields(result.Items, declaredAttributeFields, objectIdFieldName);
        var queryFields = QueryFormatter.BuildQueryFields(resource, outFields, objectIdFieldName, runtimeFields);
        foreach (var field in queryFields)
        {
            var fieldMsg = new ProtobufWriter(64);
            fieldMsg.WriteString(1, field.Name);
            fieldMsg.WriteEnum(2, MapPbfFieldType(field.Type));
            fieldMsg.WriteString(3, field.Alias ?? field.Name);
            writer.WriteMessage(13, ref fieldMsg);
            fieldMsg.Dispose();
        }

        var fieldIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < queryFields.Length; i++)
        {
            fieldIndex[queryFields[i].Name] = i;
        }

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
                effectiveZ,
                effectiveM,
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
    /// Writes a single attribute value into the Value oneof. Oneof members carry
    /// explicit presence, so the *Always writers are used: the default-skipping
    /// writers would encode 0, 0.0, false, and "" as "no member set", which clients
    /// decode as null.
    /// </summary>
    private static void WriteAttributeValue(ref ProtobufWriter writer, object value)
    {
        switch (value)
        {
            case string s:
                writer.WriteStringAlways(1, s);     // string_value
                break;
            case float f:
                writer.WriteFloatAlways(2, f);      // float_value
                break;
            case double d:
                writer.WriteDoubleAlways(3, d);     // double_value
                break;
            case int i:
                writer.WriteSInt32Always(4, i);     // sint_value
                break;
            case uint u:
                writer.WriteUInt32Always(5, u);     // uint_value
                break;
            case long l:
                writer.WriteInt64Always(6, l);      // int64_value
                break;
            case ulong ul:
                writer.WriteUInt64Always(7, ul);    // uint64_value
                break;
            case bool b:
                writer.WriteBoolAlways(9, b);       // bool_value
                break;
            case short s:
                writer.WriteSInt32Always(4, s);
                break;
            case decimal d:
                writer.WriteDoubleAlways(3, (double)d);
                break;
            case DateTime dt:
                // Esri PBF encodes dates as int64 milliseconds since epoch. The shared
                // convention handles all DateTimeKind values (the DateTimeOffset(dt, Zero)
                // constructor throws for Local-kind values).
                writer.WriteInt64Always(6, GeoServicesFieldConventions.ToEpochMilliseconds(dt));
                break;
            case DateTimeOffset dto:
                writer.WriteInt64Always(6, dto.ToUnixTimeMilliseconds());
                break;
            case Guid g:
                writer.WriteStringAlways(1, g.ToString());
                break;
            default:
                writer.WriteStringAlways(1, value.ToString());
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
        Geometry? geometry;
        try
        {
            geometry = WkbReaderCache.Get().Read(wkb);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            return; // Skip unparseable geometry (matches the f=json/f=geojson formatters)
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
    /// <remarks>
    /// Esri PBF quantization semantics (matching ArcGIS Server output and the
    /// reference decoders in github.com/Esri/arcgis-pbf):
    /// - Quantized ordinates are delta-encoded against the running accumulator;
    ///   X and Y are emitted in map-space units scaled by the quantization scale.
    /// - Each lengths[] segment (ring/path) restarts delta encoding: the first
    ///   vertex of every segment is absolute and only subsequent vertices are
    ///   deltas, so the running accumulator resets at each part boundary.
    /// </remarks>
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
                    // Each path restarts delta encoding from an absolute first vertex.
                    prevX = 0;
                    prevY = 0;
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
        // Esri ring convention (mirrors GeoServicesGeometryConverter.BuildRingCoordinates
        // on the f=json path): exterior rings clockwise, holes counter-clockwise in map
        // space. Decoders classify rings purely by winding, so stored CCW exteriors (the
        // common PostGIS orientation) would otherwise render as holes or be dropped.

        // Exterior ring
        var exterior = polygon.ExteriorRing;
        lengths.Add((uint)exterior.NumPoints);
        prevX = 0;
        prevY = 0;
        AppendCoordinateSequence(
            coords, exterior.CoordinateSequence, scale, ref prevX, ref prevY, returnZ, returnM,
            reverse: ShouldReverseRing(exterior, clockwise: true));

        // Interior rings (holes)
        for (int i = 0; i < polygon.NumInteriorRings; i++)
        {
            var ring = polygon.GetInteriorRingN(i);
            lengths.Add((uint)ring.NumPoints);
            prevX = 0;
            prevY = 0;
            AppendCoordinateSequence(
                coords, ring.CoordinateSequence, scale, ref prevX, ref prevY, returnZ, returnM,
                reverse: ShouldReverseRing(ring, clockwise: false));
        }
    }

    /// <summary>
    /// Determines whether a ring must be emitted in reverse order to honor the Esri
    /// winding convention (<paramref name="clockwise"/> = true for exterior rings,
    /// false for holes), evaluated in map space.
    /// </summary>
    private static bool ShouldReverseRing(LineString ring, bool clockwise)
    {
        if (ring.NumPoints < 4)
        {
            return false;
        }

        var isCcw = Orientation.IsCCW(ring.Coordinates);
        return clockwise == isCcw;
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

        // When the Z/M flags are set every vertex must carry the ordinate, otherwise
        // the decoder's stride no longer matches hasZ/hasM and the stream garbles.
        if (returnZ)
        {
            var z = double.IsNaN(coord.Z) ? 0d : coord.Z;
            coords.Add((long)Math.Round(z * scale));
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
        bool returnM,
        bool reverse = false)
    {
        for (int index = 0; index < sequence.Count; index++)
        {
            var i = reverse ? sequence.Count - 1 - index : index;
            long x = (long)Math.Round(sequence.GetX(i) * scale);
            long y = (long)Math.Round(sequence.GetY(i) * scale);
            coords.Add(x - prevX);
            coords.Add(y - prevY);
            prevX = x;
            prevY = y;

            if (returnZ)
            {
                var z = sequence.HasZ ? sequence.GetZ(i) : double.NaN;
                if (double.IsNaN(z))
                {
                    z = 0d;
                }

                coords.Add((long)Math.Round(z * scale));
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
    /// Detects whether any feature's WKB geometry carries Z or M ordinates by
    /// inspecting the WKB type header (EWKB dimension flags and ISO SQL/MM
    /// 1000/2000/3000 type offsets), without fully parsing the geometries.
    /// </summary>
    private static (bool HasZ, bool HasM) DetectZmOrdinates(ImmutableArray<Feature> features)
    {
        var hasZ = false;
        var hasM = false;
        foreach (var feature in features)
        {
            var wkb = feature.Geometry;
            if (wkb is not { Length: >= 5 })
            {
                continue;
            }

            var type = wkb[0] == 1
                ? BinaryPrimitives.ReadUInt32LittleEndian(wkb.AsSpan(1, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(wkb.AsSpan(1, 4));

            // EWKB flags
            hasZ |= (type & 0x80000000u) != 0;
            hasM |= (type & 0x40000000u) != 0;

            // ISO SQL/MM type offsets: +1000 = Z, +2000 = M, +3000 = ZM
            var isoDimension = (type & 0xFFFFu) / 1000u;
            hasZ |= isoDimension is 1 or 3;
            hasM |= isoDimension is 2 or 3;

            if (hasZ && hasM)
            {
                break;
            }
        }

        return (hasZ, hasM);
    }

    /// <summary>
    /// Writes the Transform message with quantization scale/translate.
    /// </summary>
    private static void WriteTransform(ref ProtobufWriter writer, bool hasZ, bool hasM)
    {
        double scale = DefaultQuantizationScale;
        var transform = new ProtobufWriter(64);

        // field 1: quantizeOriginPosition (0 = upperLeft, default).

        // field 2: scale (Scale: xScale=1, yScale=2, mScale=3, zScale=4).
        // Z/M ordinates are quantized at the same precision as X/Y, so their scales
        // must be emitted whenever those ordinates are present, or decoders dequantize
        // them with the default 0.0 scale and every Z/M collapses to 0.
        var scaleMsg = new ProtobufWriter(48);
        scaleMsg.WriteDouble(1, 1.0 / scale);   // xScale
        scaleMsg.WriteDouble(2, 1.0 / scale);   // yScale
        if (hasM)
        {
            scaleMsg.WriteDouble(3, 1.0 / scale);   // mScale
        }
        if (hasZ)
        {
            scaleMsg.WriteDouble(4, 1.0 / scale);   // zScale
        }
        transform.WriteMessage(2, ref scaleMsg);
        scaleMsg.Dispose();

        // field 3: translate (Translate: xTranslate=1, yTranslate=2, mTranslate=3,
        // zTranslate=4), all zero. Forced writes keep the Translate message non-empty
        // so it is present on the wire for decoders that dereference it directly.
        var translateMsg = new ProtobufWriter(48);
        translateMsg.WriteDoubleAlways(1, 0.0);        // xTranslate
        translateMsg.WriteDoubleAlways(2, 0.0);        // yTranslate
        if (hasM)
        {
            translateMsg.WriteDoubleAlways(3, 0.0);    // mTranslate
        }
        if (hasZ)
        {
            translateMsg.WriteDoubleAlways(4, 0.0);    // zTranslate
        }
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

    private static int MapPbfGeometryType(MetadataV2GeometryType geometryType)
        => geometryType switch
        {
            MetadataV2GeometryType.Point => 0,
            MetadataV2GeometryType.MultiPoint => 1,
            MetadataV2GeometryType.LineString => 2,
            MetadataV2GeometryType.MultiLineString => 2,
            MetadataV2GeometryType.Polygon => 3,
            MetadataV2GeometryType.MultiPolygon => 3,
            _ => 127
        };

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
    /// Maps GeoServices field type string to the Esri PBF field-type enum value.
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
