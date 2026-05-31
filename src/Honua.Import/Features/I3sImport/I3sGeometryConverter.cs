// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text.Json;
using Honua.Core.Features.Scene.Generation;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Decodes an I3S geometry buffer (binary, per-attribute-array topology) and
/// writes a glTF 2.0 binary (GLB) tile that CesiumJS can render. Positions are
/// transformed from the node-local east-north-up frame at the MBS center into
/// world ECEF (EPSG:4978).
/// </summary>
/// <remarks>
/// <para>
/// Initial slice supports the most common 3D-Object Scene Layer geometry
/// layout: PerAttributeArray topology, FLOAT32 positions/normals/uv0, optional
/// UINT8 colors. Indexed topology, Draco compression, and non-FLOAT32 attribute
/// component types are deferred — converter rejects them with a clear error
/// rather than silently producing a broken GLB.
/// </para>
/// <para>
/// The GLB layout mirrors <c>GeometryTileBuilder</c> in <c>Honua.Core</c>:
/// hand-rolled binary writer (12-byte GLB header + JSON chunk + BIN chunk),
/// no external glTF library. Materials are unlit double-sided PBR; textures
/// are not embedded in this slice.
/// </para>
/// </remarks>
internal static class I3sGeometryConverter
{
    private const int GlbHeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const uint GlbMagic = 0x46546C67; // "glTF"
    private const uint GlbVersion = 2;
    private const uint JsonChunkType = 0x4E4F534A; // "JSON"
    private const uint BinChunkType = 0x004E4942; // "BIN\0"

    /// <summary>
    /// Decodes a single I3S geometry buffer and returns a GLB byte sequence
    /// representing the same geometry positioned in world ECEF.
    /// </summary>
    /// <param name="geometryBuffer">Raw I3S geometry buffer (already decompressed).</param>
    /// <param name="schema">Geometry schema describing buffer layout.</param>
    /// <param name="mbsCenterLongitudeDegrees">MBS center longitude in WGS-84 degrees.</param>
    /// <param name="mbsCenterLatitudeDegrees">MBS center latitude in WGS-84 degrees.</param>
    /// <param name="mbsCenterHeightMeters">MBS center ellipsoidal height in meters.</param>
    /// <param name="generatorTag">Optional <c>asset.generator</c> string in the GLB.</param>
    /// <returns>GLB byte payload.</returns>
    public static byte[] BuildGlbFromI3sGeometry(
        ReadOnlySpan<byte> geometryBuffer,
        I3sGeometrySchema schema,
        double mbsCenterLongitudeDegrees,
        double mbsCenterLatitudeDegrees,
        double mbsCenterHeightMeters,
        string? generatorTag = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (!string.Equals(schema.Topology, "PerAttributeArray", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"I3S geometry topology '{schema.Topology}' is not supported by the initial slice; only PerAttributeArray is implemented.");
        }

        if (!string.Equals(schema.GeometryType, "triangles", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"I3S geometry type '{schema.GeometryType}' is not supported by the initial slice; only 'triangles' is implemented.");
        }

        if (schema.VertexAttributes is null || schema.VertexAttributes.Count == 0)
        {
            throw new InvalidOperationException("I3S geometry schema must declare at least a position vertex attribute.");
        }

        if (schema.Ordering is null || schema.Ordering.Length == 0)
        {
            throw new InvalidOperationException("I3S geometry schema must declare a vertex-attribute ordering.");
        }

        var (headerLength, vertexCount) = ReadHeader(geometryBuffer, schema);
        if (vertexCount <= 0)
        {
            throw new InvalidOperationException("I3S geometry buffer reports zero vertices; cannot build GLB.");
        }

        var offsets = ComputeAttributeOffsets(schema, headerLength, vertexCount);

        var positionAttribute = ResolveAttribute(schema, "position");
        if (positionAttribute is null)
        {
            throw new InvalidOperationException("I3S geometry schema is missing the required 'position' attribute.");
        }

        EnsureFloat32(positionAttribute, "position");
        if (positionAttribute.ValuesPerElement != 3)
        {
            throw new NotSupportedException(
                $"I3S position attribute must be a 3-component vector; saw valuesPerElement={positionAttribute.ValuesPerElement}.");
        }

        var positionsLocal = ReadFloat32(geometryBuffer, offsets["position"], vertexCount * 3);

        var rotation = ComputeEnuToEcefRotation(mbsCenterLongitudeDegrees, mbsCenterLatitudeDegrees);
        var (cx, cy, cz) = EcefCoordinateTransform.ToEcef(
            mbsCenterLongitudeDegrees, mbsCenterLatitudeDegrees, mbsCenterHeightMeters);

        var positionsEcef = new float[vertexCount * 3];
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var minZ = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        var maxZ = float.NegativeInfinity;

        for (var i = 0; i < vertexCount; i++)
        {
            var e = positionsLocal[i * 3];
            var n = positionsLocal[i * 3 + 1];
            var u = positionsLocal[i * 3 + 2];

            var xWorld = cx + rotation.M00 * e + rotation.M01 * n + rotation.M02 * u;
            var yWorld = cy + rotation.M10 * e + rotation.M11 * n + rotation.M12 * u;
            var zWorld = cz + rotation.M20 * e + rotation.M21 * n + rotation.M22 * u;

            var fx = (float)xWorld;
            var fy = (float)yWorld;
            var fz = (float)zWorld;
            positionsEcef[i * 3] = fx;
            positionsEcef[i * 3 + 1] = fy;
            positionsEcef[i * 3 + 2] = fz;

            if (fx < minX) minX = fx;
            if (fy < minY) minY = fy;
            if (fz < minZ) minZ = fz;
            if (fx > maxX) maxX = fx;
            if (fy > maxY) maxY = fy;
            if (fz > maxZ) maxZ = fz;
        }

        float[]? normalsEcef = null;
        var normalAttribute = ResolveAttribute(schema, "normal");
        if (normalAttribute is not null
            && offsets.TryGetValue("normal", out var normalOffset)
            && IsFloat32(normalAttribute)
            && normalAttribute.ValuesPerElement == 3)
        {
            var normalsLocal = ReadFloat32(geometryBuffer, normalOffset, vertexCount * 3);
            normalsEcef = new float[vertexCount * 3];
            for (var i = 0; i < vertexCount; i++)
            {
                var nx = normalsLocal[i * 3];
                var ny = normalsLocal[i * 3 + 1];
                var nz = normalsLocal[i * 3 + 2];

                var rx = rotation.M00 * nx + rotation.M01 * ny + rotation.M02 * nz;
                var ry = rotation.M10 * nx + rotation.M11 * ny + rotation.M12 * nz;
                var rz = rotation.M20 * nx + rotation.M21 * ny + rotation.M22 * nz;

                var length = Math.Sqrt(rx * rx + ry * ry + rz * rz);
                if (length > 1e-12)
                {
                    rx /= length;
                    ry /= length;
                    rz /= length;
                }

                normalsEcef[i * 3] = (float)rx;
                normalsEcef[i * 3 + 1] = (float)ry;
                normalsEcef[i * 3 + 2] = (float)rz;
            }
        }

        float[]? uvs = null;
        var uvAttribute = ResolveAttribute(schema, "uv0");
        if (uvAttribute is not null
            && offsets.TryGetValue("uv0", out var uvOffset)
            && IsFloat32(uvAttribute)
            && uvAttribute.ValuesPerElement == 2)
        {
            uvs = ReadFloat32(geometryBuffer, uvOffset, vertexCount * 2);
        }

        byte[]? colors = null;
        var colorAttribute = ResolveAttribute(schema, "color");
        if (colorAttribute is not null
            && offsets.TryGetValue("color", out var colorOffset)
            && colorAttribute.ValuesPerElement == 4
            && string.Equals(colorAttribute.ValueType, "UInt8", StringComparison.OrdinalIgnoreCase))
        {
            colors = geometryBuffer.Slice(colorOffset, vertexCount * 4).ToArray();
        }

        return AssembleGlb(
            vertexCount,
            positionsEcef,
            normalsEcef,
            uvs,
            colors,
            minX, minY, minZ, maxX, maxY, maxZ,
            generatorTag);
    }

    private static (int HeaderLength, int VertexCount) ReadHeader(ReadOnlySpan<byte> buffer, I3sGeometrySchema schema)
    {
        var offset = 0;
        var vertexCount = 0;

        if (schema.Header is not null)
        {
            foreach (var field in schema.Header)
            {
                var fieldSize = SizeOfNumericType(field.Type);
                if (offset + fieldSize > buffer.Length)
                {
                    throw new InvalidDataException("I3S geometry buffer truncated within header.");
                }

                if (string.Equals(field.Property, "vertexCount", StringComparison.OrdinalIgnoreCase))
                {
                    vertexCount = ReadUint(buffer, offset, field.Type);
                }

                offset += fieldSize;
            }
        }

        // PerAttributeArray topology without an explicit vertexCount header is
        // unusual; treat as a hard failure rather than guessing.
        if (vertexCount == 0)
        {
            throw new InvalidOperationException(
                "I3S geometry header did not provide a non-zero vertexCount; cannot decode buffer.");
        }

        return (offset, vertexCount);
    }

    private static Dictionary<string, int> ComputeAttributeOffsets(
        I3sGeometrySchema schema,
        int headerLength,
        int vertexCount)
    {
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cursor = headerLength;

        foreach (var name in schema.Ordering!)
        {
            if (!schema.VertexAttributes!.TryGetValue(name, out var attribute))
            {
                // Order references an attribute the spec didn't describe — abort.
                throw new InvalidOperationException(
                    $"I3S geometry schema ordering references undefined vertex attribute '{name}'.");
            }

            offsets[name] = cursor;
            cursor += vertexCount * attribute.ValuesPerElement * SizeOfNumericType(attribute.ValueType);
        }

        return offsets;
    }

    private static I3sVertexAttribute? ResolveAttribute(I3sGeometrySchema schema, string name)
    {
        if (schema.VertexAttributes is null)
        {
            return null;
        }

        return schema.VertexAttributes.TryGetValue(name, out var attribute) ? attribute : null;
    }

    private static int SizeOfNumericType(string type) => type switch
    {
        var t when t.Equals("Float32", StringComparison.OrdinalIgnoreCase) => 4,
        var t when t.Equals("Float64", StringComparison.OrdinalIgnoreCase) => 8,
        var t when t.Equals("UInt32", StringComparison.OrdinalIgnoreCase) => 4,
        var t when t.Equals("Int32", StringComparison.OrdinalIgnoreCase) => 4,
        var t when t.Equals("UInt16", StringComparison.OrdinalIgnoreCase) => 2,
        var t when t.Equals("Int16", StringComparison.OrdinalIgnoreCase) => 2,
        var t when t.Equals("UInt8", StringComparison.OrdinalIgnoreCase) => 1,
        var t when t.Equals("Int8", StringComparison.OrdinalIgnoreCase) => 1,
        _ => throw new NotSupportedException($"Unsupported I3S numeric type '{type}'.")
    };

    private static int ReadUint(ReadOnlySpan<byte> buffer, int offset, string type) => type switch
    {
        var t when t.Equals("UInt32", StringComparison.OrdinalIgnoreCase)
            => (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset, 4)),
        var t when t.Equals("UInt16", StringComparison.OrdinalIgnoreCase)
            => BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset, 2)),
        var t when t.Equals("UInt8", StringComparison.OrdinalIgnoreCase)
            => buffer[offset],
        var t when t.Equals("Int32", StringComparison.OrdinalIgnoreCase)
            => BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset, 4)),
        _ => throw new NotSupportedException($"Unsupported I3S header numeric type '{type}'.")
    };

    private static float[] ReadFloat32(ReadOnlySpan<byte> buffer, int offset, int count)
    {
        var byteCount = count * 4;
        if (offset + byteCount > buffer.Length)
        {
            throw new InvalidDataException("I3S geometry buffer truncated within vertex attribute.");
        }

        var result = new float[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(offset + i * 4, 4));
        }
        return result;
    }

    private static bool IsFloat32(I3sVertexAttribute attribute)
        => string.Equals(attribute.ValueType, "Float32", StringComparison.OrdinalIgnoreCase);

    private static void EnsureFloat32(I3sVertexAttribute attribute, string name)
    {
        if (!IsFloat32(attribute))
        {
            throw new NotSupportedException(
                $"I3S {name} attribute valueType '{attribute.ValueType}' is not supported by the initial slice; only Float32 is implemented.");
        }
    }

    /// <summary>
    /// 3×3 rotation matrix in row-major layout. M[row][col] notation.
    /// </summary>
    private readonly record struct EnuToEcefRotation(
        double M00, double M01, double M02,
        double M10, double M11, double M12,
        double M20, double M21, double M22);

    private static EnuToEcefRotation ComputeEnuToEcefRotation(double longitudeDeg, double latitudeDeg)
    {
        var lonRad = longitudeDeg * Math.PI / 180.0;
        var latRad = latitudeDeg * Math.PI / 180.0;
        var sinLon = Math.Sin(lonRad);
        var cosLon = Math.Cos(lonRad);
        var sinLat = Math.Sin(latRad);
        var cosLat = Math.Cos(latRad);

        // ENU → ECEF rotation (transpose of the standard ECEF → ENU rotation).
        return new EnuToEcefRotation(
            -sinLon, -sinLat * cosLon, cosLat * cosLon,
            cosLon, -sinLat * sinLon, cosLat * sinLon,
            0.0, cosLat, sinLat);
    }

    private static byte[] AssembleGlb(
        int vertexCount,
        float[] positions,
        float[]? normals,
        float[]? uvs,
        byte[]? colors,
        float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ,
        string? generatorTag)
    {
        var positionBytes = FloatsToBytes(positions);
        byte[]? normalBytes = normals is null ? null : FloatsToBytes(normals);
        byte[]? uvBytes = uvs is null ? null : FloatsToBytes(uvs);
        byte[]? colorBytes = colors;

        var bufferViewLengths = new List<int> { positionBytes.Length };
        if (normalBytes is not null) bufferViewLengths.Add(normalBytes.Length);
        if (uvBytes is not null) bufferViewLengths.Add(uvBytes.Length);
        if (colorBytes is not null) bufferViewLengths.Add(colorBytes.Length);

        // Compute padded offsets (each buffer view starts at a 4-byte boundary).
        var bufferViewOffsets = new int[bufferViewLengths.Count];
        var binaryLength = 0;
        for (var i = 0; i < bufferViewLengths.Count; i++)
        {
            var pad = (4 - (binaryLength & 3)) & 3;
            binaryLength += pad;
            bufferViewOffsets[i] = binaryLength;
            binaryLength += bufferViewLengths[i];
        }
        // Pad the binary chunk to a multiple of 4 bytes for the GLB spec.
        var binaryPad = (4 - (binaryLength & 3)) & 3;
        binaryLength += binaryPad;

        var jsonBytes = BuildJsonChunk(
            vertexCount,
            bufferViewLengths,
            bufferViewOffsets,
            hasNormals: normalBytes is not null,
            hasUvs: uvBytes is not null,
            hasColors: colorBytes is not null,
            minX, minY, minZ, maxX, maxY, maxZ,
            binaryLength,
            generatorTag);
        var paddedJson = PadJsonToFourBytes(jsonBytes);

        var totalLength = GlbHeaderLength
            + ChunkHeaderLength + paddedJson.Length
            + ChunkHeaderLength + binaryLength;

        var glb = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)totalLength);

        var cursor = GlbHeaderLength;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(cursor, 4), (uint)paddedJson.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(cursor + 4, 4), JsonChunkType);
        cursor += ChunkHeaderLength;
        paddedJson.CopyTo(glb.AsSpan(cursor, paddedJson.Length));
        cursor += paddedJson.Length;

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(cursor, 4), (uint)binaryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(cursor + 4, 4), BinChunkType);
        cursor += ChunkHeaderLength;

        // Emit each buffer view at its computed offset (with leading padding zeroes
        // already implicit in the zero-initialized array).
        var viewIndex = 0;
        CopyView(glb, cursor + bufferViewOffsets[viewIndex++], positionBytes);
        if (normalBytes is not null)
        {
            CopyView(glb, cursor + bufferViewOffsets[viewIndex++], normalBytes);
        }
        if (uvBytes is not null)
        {
            CopyView(glb, cursor + bufferViewOffsets[viewIndex++], uvBytes);
        }
        if (colorBytes is not null)
        {
            CopyView(glb, cursor + bufferViewOffsets[viewIndex], colorBytes);
        }

        return glb;
    }

    private static void CopyView(byte[] destination, int offset, byte[] source)
        => source.CopyTo(destination.AsSpan(offset, source.Length));

    private static byte[] FloatsToBytes(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }

    private static byte[] PadJsonToFourBytes(byte[] json)
    {
        var padding = (4 - (json.Length & 3)) & 3;
        if (padding == 0) return json;
        var padded = new byte[json.Length + padding];
        json.CopyTo(padded, 0);
        for (var i = 0; i < padding; i++)
        {
            padded[json.Length + i] = 0x20; // ASCII space, per GLB spec
        }
        return padded;
    }

    private static byte[] BuildJsonChunk(
        int vertexCount,
        List<int> bufferViewLengths,
        int[] bufferViewOffsets,
        bool hasNormals,
        bool hasUvs,
        bool hasColors,
        float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ,
        int totalBinaryLength,
        string? generatorTag)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("asset");
            writer.WriteString("version", "2.0");
            if (!string.IsNullOrEmpty(generatorTag))
            {
                writer.WriteString("generator", generatorTag);
            }
            writer.WriteEndObject();

            writer.WriteStartArray("scenes");
            writer.WriteStartObject();
            writer.WriteStartArray("nodes");
            writer.WriteNumberValue(0);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteNumber("scene", 0);

            writer.WriteStartArray("nodes");
            writer.WriteStartObject();
            writer.WriteNumber("mesh", 0);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("meshes");
            writer.WriteStartObject();
            writer.WriteStartArray("primitives");
            writer.WriteStartObject();
            writer.WriteStartObject("attributes");
            writer.WriteNumber("POSITION", 0);
            var nextAccessor = 1;
            if (hasNormals)
            {
                writer.WriteNumber("NORMAL", nextAccessor++);
            }
            if (hasUvs)
            {
                writer.WriteNumber("TEXCOORD_0", nextAccessor++);
            }
            if (hasColors)
            {
                writer.WriteNumber("COLOR_0", nextAccessor++);
            }
            writer.WriteEndObject();
            writer.WriteNumber("mode", 4); // TRIANGLES
            writer.WriteNumber("material", 0);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("accessors");
            // Position accessor (with min/max)
            writer.WriteStartObject();
            writer.WriteNumber("bufferView", 0);
            writer.WriteNumber("componentType", 5126); // FLOAT
            writer.WriteNumber("count", vertexCount);
            writer.WriteString("type", "VEC3");
            writer.WriteStartArray("min");
            writer.WriteNumberValue(minX);
            writer.WriteNumberValue(minY);
            writer.WriteNumberValue(minZ);
            writer.WriteEndArray();
            writer.WriteStartArray("max");
            writer.WriteNumberValue(maxX);
            writer.WriteNumberValue(maxY);
            writer.WriteNumberValue(maxZ);
            writer.WriteEndArray();
            writer.WriteEndObject();

            var bufferViewIndex = 1;
            if (hasNormals)
            {
                writer.WriteStartObject();
                writer.WriteNumber("bufferView", bufferViewIndex++);
                writer.WriteNumber("componentType", 5126);
                writer.WriteNumber("count", vertexCount);
                writer.WriteString("type", "VEC3");
                writer.WriteEndObject();
            }
            if (hasUvs)
            {
                writer.WriteStartObject();
                writer.WriteNumber("bufferView", bufferViewIndex++);
                writer.WriteNumber("componentType", 5126);
                writer.WriteNumber("count", vertexCount);
                writer.WriteString("type", "VEC2");
                writer.WriteEndObject();
            }
            if (hasColors)
            {
                writer.WriteStartObject();
                writer.WriteNumber("bufferView", bufferViewIndex);
                writer.WriteNumber("componentType", 5121); // UNSIGNED_BYTE
                writer.WriteBoolean("normalized", true);
                writer.WriteNumber("count", vertexCount);
                writer.WriteString("type", "VEC4");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("materials");
            writer.WriteStartObject();
            writer.WriteString("name", "honua_i3s_default");
            writer.WriteBoolean("doubleSided", true);
            writer.WriteStartObject("pbrMetallicRoughness");
            writer.WriteStartArray("baseColorFactor");
            writer.WriteNumberValue(1.0);
            writer.WriteNumberValue(1.0);
            writer.WriteNumberValue(1.0);
            writer.WriteNumberValue(1.0);
            writer.WriteEndArray();
            writer.WriteNumber("metallicFactor", 0.0);
            writer.WriteNumber("roughnessFactor", 1.0);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("bufferViews");
            for (var i = 0; i < bufferViewLengths.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("buffer", 0);
                writer.WriteNumber("byteOffset", bufferViewOffsets[i]);
                writer.WriteNumber("byteLength", bufferViewLengths[i]);
                writer.WriteNumber("target", 34962); // ARRAY_BUFFER
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("buffers");
            writer.WriteStartObject();
            writer.WriteNumber("byteLength", totalBinaryLength);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
