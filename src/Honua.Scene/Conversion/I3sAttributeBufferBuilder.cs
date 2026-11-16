// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

public static class I3sAttributeBufferBuilder
{
    public const string ObjectIdFieldKey = "f_0";

    public const string Oid32ValueType = "Oid32";

    public const string Float64ValueType = "Float64";

    public const string StringValueType = "String";

    public const int Float64ValueBytes = 8;

    public const int ByteCountValueBytes = 4;

    public const int StringHeaderBytes = 8;

    public const int HeaderBytes = 8;

    public const int Oid32ValueBytes = 4;

    public static byte[]? Build(I3sTranscodedGeometry geometry, I3sAttributeStorageInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);

        // Only the synthetic OBJECTID (Oid32) field is materialisable from the
        // served geometry today; user-attribute fields need the deferred
        // EXT_structural_metadata property-table decode.
        if (!string.Equals(field.Key, ObjectIdFieldKey, StringComparison.Ordinal)
            || !string.Equals(field.AttributeValues?.ValueType, Oid32ValueType, StringComparison.Ordinal))
        {
            return null;
        }

        var ids = ReadFeatureIds(geometry);
        return PackOid32(ids);
    }

    public static byte[]? Build(IReadOnlyList<SceneFeature> features, I3sAttributeStorageInfo field)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(field);

        var valueType = field.AttributeValues?.ValueType;

        if (string.Equals(field.Key, ObjectIdFieldKey, StringComparison.Ordinal)
            && string.Equals(valueType, Oid32ValueType, StringComparison.Ordinal))
        {
            var ids = new int[features.Count];
            for (var i = 0; i < features.Count; i++)
            {
                var rawId = features[i].Id;
                // BH-S-03: guard against silent truncation — Oid32 only holds values in
                // [0, Int32.MaxValue]. A feature ID that exceeds this range (e.g. OSM building
                // IDs) would previously silently wrap to a negative/wrong value, causing ArcGIS
                // identify to return wrong attributes. Fail loudly so the caller can remap IDs.
                if (rawId < 0 || rawId > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Feature ID {rawId} exceeds the I3S Oid32 range (0–{int.MaxValue}). " +
                        "Publish the scene with a remapped integer ID field to serve it via I3S.");
                }
                ids[i] = (int)rawId;
            }

            return PackOid32(ids);
        }

        // User fields are keyed on the attribute name the schema builder assigned.
        var attributeKey = field.Name;
        if (string.IsNullOrEmpty(attributeKey))
        {
            return null;
        }

        if (string.Equals(valueType, Float64ValueType, StringComparison.Ordinal))
        {
            return PackFloat64(features, attributeKey);
        }

        if (string.Equals(valueType, StringValueType, StringComparison.Ordinal))
        {
            return PackStrings(features, attributeKey);
        }

        return null;
    }

    private static int[] ReadFeatureIds(I3sTranscodedGeometry geometry)
    {
        var buffer = geometry.Buffer;
        var featureCount = geometry.FeatureCount;
        var ids = new int[featureCount];

        // Ids are a contiguous UInt64 array immediately after the vertex
        // attributes. faceRange follows the whole id array and is not interleaved
        // with the ids (I3S PerAttributeArray / geometryBuffer order).
        var featureIdOffset = I3sGeometryTranscoder.HeaderBytes
            + (geometry.VertexCount * I3sGeometryTranscoder.VertexStrideBytes);

        var span = buffer.AsSpan();
        for (var i = 0; i < featureCount; i++)
        {
            var recordOffset = featureIdOffset + (i * I3sGeometryTranscoder.FeatureIdBytes);
            var id = BinaryPrimitives.ReadUInt64LittleEndian(
                span.Slice(recordOffset, I3sGeometryTranscoder.FeatureIdBytes));
            // BH-S-03: guard against silent truncation — Oid32 only holds values up to
            // Int32.MaxValue. Fail loudly if a geometry transcoded with a large ID was
            // somehow persisted so the caller can diagnose the mismatch.
            if (id > (ulong)int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Feature ID {id} exceeds the I3S Oid32 range (0–{int.MaxValue}). " +
                    "Publish the scene with a remapped integer ID field to serve it via I3S.");
            }
            ids[i] = (int)id;
        }

        return ids;
    }

    private static byte[] PackOid32(int[] ids)
    {
        var buffer = new byte[HeaderBytes + (ids.Length * Oid32ValueBytes)];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)ids.Length);
        // span[4..8] reserved (zero) for 8-byte value-array alignment.

        var offset = HeaderBytes;
        foreach (var id in ids)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, Oid32ValueBytes), id);
            offset += Oid32ValueBytes;
        }

        return buffer;
    }

    private static byte[] PackFloat64(IReadOnlyList<SceneFeature> features, string attributeKey)
    {
        var buffer = new byte[HeaderBytes + (features.Count * Float64ValueBytes)];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)features.Count);
        // span[4..8] reserved (zero) for 8-byte value-array alignment.

        var offset = HeaderBytes;
        foreach (var value in features.Select(feature => TryGetNumeric(feature, attributeKey) ?? 0.0))
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(offset, Float64ValueBytes), value);
            offset += Float64ValueBytes;
        }

        return buffer;
    }

    private static byte[] PackStrings(IReadOnlyList<SceneFeature> features, string attributeKey)
    {
        var count = features.Count;
        var utf8 = new byte[count][];
        var valuesByteCount = 0;
        for (var i = 0; i < count; i++)
        {
            var text = TryGetString(features[i], attributeKey);
            // Each value carries a trailing NUL, so the byte-count and the blob
            // both include the terminator (the layout ArcGIS string files use).
            var encoded = new byte[Encoding.UTF8.GetByteCount(text) + 1];
            Encoding.UTF8.GetBytes(text, encoded);
            utf8[i] = encoded;
            valuesByteCount += encoded.Length;
        }

        var byteCountsBytes = count * ByteCountValueBytes;
        var buffer = new byte[StringHeaderBytes + byteCountsBytes + valuesByteCount];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)valuesByteCount);

        var byteCountsOffset = StringHeaderBytes;
        var valuesOffset = StringHeaderBytes + byteCountsBytes;
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                span.Slice(byteCountsOffset, ByteCountValueBytes),
                (uint)utf8[i].Length);
            byteCountsOffset += ByteCountValueBytes;

            utf8[i].CopyTo(span.Slice(valuesOffset, utf8[i].Length));
            valuesOffset += utf8[i].Length;
        }

        return buffer;
    }

    internal static double? TryGetNumeric(SceneFeature feature, string attributeKey)
    {
        if (!feature.Attributes.TryGetValue(attributeKey, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            byte b => b,
            sbyte b => b,
            short s => s,
            ushort s => s,
            int i => i,
            uint i => i,
            long l => l,
            ulong l => l,
            float f => f,
            double d => d,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string TryGetString(SceneFeature feature, string attributeKey)
    {
        if (!feature.Attributes.TryGetValue(attributeKey, out var value) || value is null)
        {
            return string.Empty;
        }

        return value as string
            ?? Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? string.Empty;
    }
}
