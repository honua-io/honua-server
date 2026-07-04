// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Honua.Core.Features.Scene.Conversion;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Scene;

/// <summary>
/// Builds the I3S per-field attribute binary file served at
/// <c>nodes/{nodeId}/attributes/{fieldKey}/0</c> (#1811). This file is what an
/// ArcGIS SceneLayer client reads to satisfy <c>identify</c> — it pairs the
/// served node geometry's feature ids with the layer's
/// <c>attributeStorageInfo</c> so a picked feature resolves to its attribute
/// values.
/// </summary>
/// <remarks>
/// <para>
/// The attribute values are derived <b>honestly from the geometry that is
/// actually served</b>: the transcoded geometry buffer carries a feature section
/// (per-feature <c>id</c> + vertex range) emitted by
/// <see cref="I3sGeometryTranscoder"/>, so the attribute file is read back from
/// that same buffer rather than from a parallel data path. This guarantees the
/// attribute order matches the geometry's feature order, which is exactly the
/// invariant an I3S client relies on when mapping a picked vertex's feature id to
/// an attribute row.
/// </para>
/// <para>
/// <b>Scope (#1811 increment).</b> The synthetic <c>OBJECTID</c> field (<c>f_0</c>,
/// <c>Oid32</c>) every served 3D Object node carries is materialised here as a
/// fixed-width attribute file: an 8-byte-aligned header (<c>count</c> +
/// reserved, little-endian UInt32) followed by <c>count</c> little-endian Int32
/// object ids, matching the <c>attributeStorageInfo</c> ordering
/// <c>["attributeValues"]</c> and value type <c>Oid32</c>. Per-value user
/// attribute fields (strings, numerics) require decoding the baked glTF
/// <c>EXT_structural_metadata</c> property tables — that decode stays the
/// deferred hard-lane work, so those fields return <see langword="null"/> here
/// (the endpoint answers a deterministic 404 rather than fabricating values).
/// </para>
/// </remarks>
public static class I3sAttributeBufferBuilder
{
    /// <summary>
    /// The descriptor's default key for the synthetic <c>OBJECTID</c> field every
    /// served 3D Object node carries.
    /// </summary>
    public const string ObjectIdFieldKey = "f_0";

    /// <summary>The I3S Oid32 value type advertised for the OBJECTID field.</summary>
    public const string Oid32ValueType = "Oid32";

    /// <summary>The I3S Float64 value type advertised for numeric user fields.</summary>
    public const string Float64ValueType = "Float64";

    /// <summary>The I3S String value type advertised for variable-length user fields.</summary>
    public const string StringValueType = "String";

    /// <summary>Bytes per <c>Float64</c> value (one little-endian double).</summary>
    public const int Float64ValueBytes = 8;

    /// <summary>Bytes per <c>attributeByteCounts</c> entry (one little-endian UInt32).</summary>
    public const int ByteCountValueBytes = 4;

    /// <summary>
    /// Header bytes for a variable-length (string) attribute file: <c>count</c>
    /// (UInt32) plus <c>attributeValuesByteCount</c> (UInt32), matching the I3S
    /// string-field ordering <c>["attributeByteCounts", "attributeValues"]</c>.
    /// </summary>
    public const int StringHeaderBytes = 8;

    /// <summary>
    /// Header bytes for a fixed-width attribute file: <c>count</c> (UInt32) plus a
    /// reserved UInt32 so the value array starts on an 8-byte boundary, matching
    /// the alignment ArcGIS uses for fixed-size attribute resources.
    /// </summary>
    public const int HeaderBytes = 8;

    /// <summary>Bytes per <c>Oid32</c> value (one little-endian Int32).</summary>
    public const int Oid32ValueBytes = 4;

    /// <summary>
    /// Builds the binary attribute file for one attribute-storage field of a
    /// served scene node, or <see langword="null"/> when the field cannot be
    /// materialised from the served geometry (so the endpoint can answer an
    /// honest 404 instead of fabricating values).
    /// </summary>
    /// <param name="geometry">
    /// The node geometry already transcoded for the node's
    /// <c>geometries/0</c> resource. Its feature section supplies the per-feature
    /// ids the attribute file is keyed on.
    /// </param>
    /// <param name="field">The attribute-storage descriptor for the requested field.</param>
    /// <returns>
    /// The attribute file bytes, or <see langword="null"/> when the field is not a
    /// servable fixed-width <c>OBJECTID</c> field.
    /// </returns>
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

    /// <summary>
    /// Builds the binary attribute file for one attribute-storage field directly
    /// from the ordered scene features (the same source the geometry transcoder
    /// consumes), or <see langword="null"/> when the field's value type is not a
    /// servable I3S attribute layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike the geometry-keyed overload, this path materialises <b>user
    /// attribute values</b> from each feature's projected
    /// <see cref="SceneFeature.Attributes"/> bag, keyed on the field's name (the
    /// attribute key <see cref="I3sAttributeSchemaBuilder"/> assigned the field
    /// to). The feature order is the canonical OBJECTID order the served geometry
    /// feature section also uses, so a picked feature resolves to the same row in
    /// every per-field file.
    /// </para>
    /// <para>
    /// Three I3S value layouts are emitted: <c>Oid32</c> (object ids),
    /// <c>Float64</c> (numeric values; a missing/non-numeric value encodes as
    /// <c>0</c>), and <c>String</c> (UTF-8 with a per-value byte-count header; a
    /// missing value encodes as the empty string). Any other value type returns
    /// <see langword="null"/> so the endpoint answers an honest 404.
    /// </para>
    /// </remarks>
    /// <param name="features">The ordered scene features supplying attribute values.</param>
    /// <param name="field">The attribute-storage descriptor for the requested field.</param>
    /// <returns>The attribute file bytes, or <see langword="null"/> for an unsupported field.</returns>
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

    /// <summary>
    /// Reads the per-feature object ids out of the transcoded geometry's trailing
    /// feature section using the buffer layout published by
    /// <see cref="I3sGeometryTranscoder"/> (so the binary format stays
    /// single-sourced with the transcoder that writes it).
    /// </summary>
    private static int[] ReadFeatureIds(I3sTranscodedGeometry geometry)
    {
        var buffer = geometry.Buffer;
        var featureCount = geometry.FeatureCount;
        var ids = new int[featureCount];

        var featureSectionOffset = I3sGeometryTranscoder.HeaderBytes
            + (geometry.VertexCount * I3sGeometryTranscoder.VertexStrideBytes);

        var span = buffer.AsSpan();
        for (var i = 0; i < featureCount; i++)
        {
            var recordOffset = featureSectionOffset + (i * I3sGeometryTranscoder.FeatureRecordBytes);
            var id = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(recordOffset, 8));
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

    /// <summary>
    /// Packs a fixed-width <c>Float64</c> attribute file: an 8-byte header
    /// (<c>count</c> + reserved, so the value array starts 8-byte aligned)
    /// followed by <c>count</c> little-endian doubles. A missing or non-numeric
    /// value encodes as <c>0</c>.
    /// </summary>
    private static byte[] PackFloat64(IReadOnlyList<SceneFeature> features, string attributeKey)
    {
        var buffer = new byte[HeaderBytes + (features.Count * Float64ValueBytes)];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], (uint)features.Count);
        // span[4..8] reserved (zero) for 8-byte value-array alignment.

        var offset = HeaderBytes;
        foreach (var feature in features)
        {
            var value = TryGetNumeric(feature, attributeKey) ?? 0.0;
            BinaryPrimitives.WriteDoubleLittleEndian(span.Slice(offset, Float64ValueBytes), value);
            offset += Float64ValueBytes;
        }

        return buffer;
    }

    /// <summary>
    /// Packs a variable-length <c>String</c> attribute file: an 8-byte header
    /// (<c>count</c> + <c>attributeValuesByteCount</c>), then a <c>UInt32</c>
    /// byte-count per value (UTF-8 length including the trailing NUL), then the
    /// NUL-terminated UTF-8 value blob. A missing value encodes as the empty
    /// string (a single NUL).
    /// </summary>
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

    /// <summary>
    /// Reads a feature's attribute value as a double when it is present and
    /// numeric, or <see langword="null"/> otherwise. Numeric-looking strings are
    /// parsed invariantly so a string-typed projection of a numeric column still
    /// resolves a value.
    /// </summary>
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
