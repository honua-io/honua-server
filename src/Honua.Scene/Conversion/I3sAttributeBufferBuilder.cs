// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Scene.Conversion;

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
            // Oid32 narrows the 64-bit feature id to the 32-bit object-id space
            // Esri identify uses; hosted scene object ids fit in 32 bits.
            ids[i] = unchecked((int)id);
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
}
