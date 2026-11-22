// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Core.Features.Scene.Generation;

public static class ObjToGlbConverter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint GlbVersion = 2;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinChunkType = 0x004E4942;
    private const int MaxVertices = 500_000;
    private const int MaxFaces = 500_000;

    public static ObjGlbConversionResult Convert(byte[] objBytes, string? generatorTag = null)
    {
        ArgumentNullException.ThrowIfNull(objBytes);
        if (objBytes.Length == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Wavefront OBJ payload is empty.");
        }

        var text = Encoding.UTF8.GetString(objBytes);
        var sourcePositions = new List<(float X, float Y, float Z)>();
        var sourceTexCoords = new List<(float U, float V)>();
        var sourceNormals = new List<(float X, float Y, float Z)>();
        var outPositions = new List<float>();
        var outNormals = new List<float>();
        var outTexCoords = new List<float>();
        var hasAnyTexCoord = false;
        var faceCount = 0;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed.StartsWith("v ", StringComparison.Ordinal) || trimmed.StartsWith("v\t", StringComparison.Ordinal))
            {
                ParseVec3(trimmed[1..], sourcePositions, "vertex position");
                if (sourcePositions.Count > MaxVertices)
                {
                    throw new ValidationException(
                        $"{SceneGenerationErrorCodes.ModelAssetInvalid}: OBJ exceeds the {MaxVertices}-vertex limit.");
                }
            }
            else if (trimmed.StartsWith("vt ", StringComparison.Ordinal) || trimmed.StartsWith("vt\t", StringComparison.Ordinal))
            {
                ParseVec2(trimmed[2..], sourceTexCoords);
            }
            else if (trimmed.StartsWith("vn ", StringComparison.Ordinal) || trimmed.StartsWith("vn\t", StringComparison.Ordinal))
            {
                ParseVec3(trimmed[2..], sourceNormals, "vertex normal");
            }
            else if (trimmed.StartsWith("f ", StringComparison.Ordinal) || trimmed.StartsWith("f\t", StringComparison.Ordinal))
            {
                ParseFace(
                    trimmed[1..],
                    sourcePositions,
                    sourceTexCoords,
                    sourceNormals,
                    outPositions,
                    outNormals,
                    outTexCoords,
                    ref hasAnyTexCoord,
                    ref faceCount);
            }
        }

        var vertexCount = outPositions.Count / 3;
        if (vertexCount == 0 || faceCount == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Wavefront OBJ contains no renderable triangle faces.");
        }

        var bounds = ComputeBounds(outPositions);
        var glb = BuildGlbBytes(
            outPositions,
            outNormals,
            hasAnyTexCoord ? outTexCoords : null,
            bounds,
            generatorTag);

        return new ObjGlbConversionResult(glb, bounds, vertexCount, faceCount);
    }

    private static void ParseVec3(
        ReadOnlySpan<char> payload,
        List<(float X, float Y, float Z)> target,
        string label)
    {
        var tokens = payload.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3
            || !float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Invalid OBJ {label} entry.");
        }

        target.Add((x, y, z));
    }

    private static void ParseVec2(ReadOnlySpan<char> payload, List<(float U, float V)> target)
    {
        var tokens = payload.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2
            || !float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var u)
            || !float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            || !float.IsFinite(u) || !float.IsFinite(v))
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Invalid OBJ texture coordinate entry.");
        }

        target.Add((u, 1.0f - v));
    }

    private static void ParseFace(
        ReadOnlySpan<char> payload,
        List<(float X, float Y, float Z)> positions,
        List<(float U, float V)> texCoords,
        List<(float X, float Y, float Z)> normals,
        List<float> outPositions,
        List<float> outNormals,
        List<float> outTexCoords,
        ref bool hasAnyTexCoord,
        ref int faceCount)
    {
        var tokens = payload.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: OBJ face must reference at least 3 vertices.");
        }

        var corners = new (int Pos, int Tex, int Norm)[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var parts = tokens[i].Split('/');
            var posIdx = ResolveObjIndex(parts[0], positions.Count, "position");
            var texIdx = parts.Length > 1 && parts[1].Length > 0
                ? ResolveObjIndex(parts[1], texCoords.Count, "texcoord")
                : -1;
            var normIdx = parts.Length > 2 && parts[2].Length > 0
                ? ResolveObjIndex(parts[2], normals.Count, "normal")
                : -1;
            corners[i] = (posIdx, texIdx, normIdx);
            if (texIdx >= 0)
            {
                hasAnyTexCoord = true;
            }
        }

        // Fan-triangulate polygons with >= 3 vertices.
        for (var i = 1; i < corners.Length - 1; i++)
        {
            var c0 = corners[0];
            var c1 = corners[i];
            var c2 = corners[i + 1];

            var p0 = positions[c0.Pos];
            var p1 = positions[c1.Pos];
            var p2 = positions[c2.Pos];

            var faceNormal = ComputeFaceNormal(p0, p1, p2);
            if (faceNormal is null)
            {
                continue;
            }

            if (++faceCount > MaxFaces)
            {
                throw new ValidationException(
                    $"{SceneGenerationErrorCodes.ModelAssetInvalid}: OBJ exceeds the {MaxFaces}-triangle limit.");
            }

            EmitCorner(p0, c0.Norm >= 0 ? Normalize(normals[c0.Norm], faceNormal.Value) : faceNormal.Value,
                c0.Tex >= 0 ? texCoords[c0.Tex] : (0f, 0f), outPositions, outNormals, outTexCoords);
            EmitCorner(p1, c1.Norm >= 0 ? Normalize(normals[c1.Norm], faceNormal.Value) : faceNormal.Value,
                c1.Tex >= 0 ? texCoords[c1.Tex] : (0f, 0f), outPositions, outNormals, outTexCoords);
            EmitCorner(p2, c2.Norm >= 0 ? Normalize(normals[c2.Norm], faceNormal.Value) : faceNormal.Value,
                c2.Tex >= 0 ? texCoords[c2.Tex] : (0f, 0f), outPositions, outNormals, outTexCoords);
        }
    }

    private static int ResolveObjIndex(string raw, int count, string label)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: Invalid OBJ {label} index '{raw}'.");
        }

        var resolved = parsed > 0 ? parsed - 1 : count + parsed;
        if (resolved < 0 || resolved >= count)
        {
            throw new ValidationException(
                $"{SceneGenerationErrorCodes.ModelAssetInvalid}: OBJ {label} index '{raw}' is out of range.");
        }

        return resolved;
    }

    private static (float X, float Y, float Z)? ComputeFaceNormal(
        (float X, float Y, float Z) a,
        (float X, float Y, float Z) b,
        (float X, float Y, float Z) c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        var nx = uy * vz - uz * vy;
        var ny = uz * vx - ux * vz;
        var nz = ux * vy - uy * vx;
        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!double.IsFinite(len) || len < 1e-12)
        {
            return null;
        }

        return ((float)(nx / len), (float)(ny / len), (float)(nz / len));
    }

    private static (float X, float Y, float Z) Normalize(
        (float X, float Y, float Z) vector,
        (float X, float Y, float Z) fallback)
    {
        var len = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
        if (!double.IsFinite(len) || len < 1e-12)
        {
            return fallback;
        }

        return ((float)(vector.X / len), (float)(vector.Y / len), (float)(vector.Z / len));
    }

    private static void EmitCorner(
        (float X, float Y, float Z) pos,
        (float X, float Y, float Z) norm,
        (float U, float V) uv,
        List<float> outPositions,
        List<float> outNormals,
        List<float> outTexCoords)
    {
        outPositions.Add(pos.X);
        outPositions.Add(pos.Y);
        outPositions.Add(pos.Z);
        outNormals.Add(norm.X);
        outNormals.Add(norm.Y);
        outNormals.Add(norm.Z);
        outTexCoords.Add(uv.U);
        outTexCoords.Add(uv.V);
    }

    private static ModelLocalBounds ComputeBounds(List<float> positions)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (var i = 0; i < positions.Count; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            var z = positions[i + 2];
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (z < minZ) minZ = z;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
            if (z > maxZ) maxZ = z;
        }

        return new ModelLocalBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static byte[] BuildGlbBytes(
        List<float> positions,
        List<float> normals,
        List<float>? texCoords,
        ModelLocalBounds bounds,
        string? generatorTag)
    {
        var vertexCount = positions.Count / 3;
        var posBytes = WriteFloats(positions);
        var normBytes = WriteFloats(normals);
        var uvBytes = texCoords is not null ? WriteFloats(texCoords) : null;

        var posOffset = 0;
        var normOffset = posBytes.Length;
        var uvOffset = normOffset + normBytes.Length;
        var totalBinUnpadded = uvOffset + (uvBytes?.Length ?? 0);
        var totalBinPadded = (totalBinUnpadded + 3) / 4 * 4;
        var binChunk = new byte[totalBinPadded];
        posBytes.CopyTo(binChunk, posOffset);
        normBytes.CopyTo(binChunk, normOffset);
        uvBytes?.CopyTo(binChunk, uvOffset);

        using var jsonStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(jsonStream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("asset");
            writer.WriteString("version", "2.0");
            if (!string.IsNullOrWhiteSpace(generatorTag))
            {
                writer.WriteString("generator", generatorTag);
            }
            writer.WriteEndObject();

            writer.WriteNumber("scene", 0);
            writer.WriteStartArray("scenes");
            writer.WriteStartObject();
            writer.WriteStartArray("nodes");
            writer.WriteNumberValue(0);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

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
            writer.WriteNumber("NORMAL", 1);
            if (uvBytes is not null)
            {
                writer.WriteNumber("TEXCOORD_0", 2);
            }
            writer.WriteEndObject();
            writer.WriteNumber("mode", 4);
            writer.WriteNumber("material", 0);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartArray("materials");
            writer.WriteStartObject();
            writer.WriteString("name", "honua_obj_default");
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

            writer.WriteStartArray("accessors");
            WriteAccessor(writer, 0, vertexCount, "VEC3", bounds);
            WriteAccessor(writer, 1, vertexCount, "VEC3", null);
            if (uvBytes is not null)
            {
                WriteAccessor(writer, 2, vertexCount, "VEC2", null);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("bufferViews");
            WriteBufferView(writer, posOffset, posBytes.Length);
            WriteBufferView(writer, normOffset, normBytes.Length);
            if (uvBytes is not null)
            {
                WriteBufferView(writer, uvOffset, uvBytes.Length);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("buffers");
            writer.WriteStartObject();
            writer.WriteNumber("byteLength", totalBinUnpadded);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        var rawJson = jsonStream.ToArray();
        var paddedJsonLength = (rawJson.Length + 3) / 4 * 4;
        var totalLength = 12 + 8 + paddedJsonLength + 8 + binChunk.Length;
        var glb = new byte[totalLength];

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)totalLength);

        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)paddedJsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), JsonChunkType);
        glb.AsSpan(20, paddedJsonLength).Fill(0x20);
        rawJson.CopyTo(glb, 20);

        var binHeaderOffset = 20 + paddedJsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binHeaderOffset, 4), (uint)binChunk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(binHeaderOffset + 4, 4), BinChunkType);
        binChunk.CopyTo(glb, binHeaderOffset + 8);

        return glb;
    }

    private static void WriteAccessor(
        Utf8JsonWriter writer,
        int bufferView,
        int count,
        string type,
        ModelLocalBounds? bounds)
    {
        writer.WriteStartObject();
        writer.WriteNumber("bufferView", bufferView);
        writer.WriteNumber("componentType", 5126);
        writer.WriteNumber("count", count);
        writer.WriteString("type", type);
        if (bounds is { } b)
        {
            writer.WriteStartArray("min");
            writer.WriteNumberValue(b.MinX);
            writer.WriteNumberValue(b.MinY);
            writer.WriteNumberValue(b.MinZ);
            writer.WriteEndArray();
            writer.WriteStartArray("max");
            writer.WriteNumberValue(b.MaxX);
            writer.WriteNumberValue(b.MaxY);
            writer.WriteNumberValue(b.MaxZ);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static void WriteBufferView(Utf8JsonWriter writer, int offset, int length)
    {
        writer.WriteStartObject();
        writer.WriteNumber("buffer", 0);
        writer.WriteNumber("byteOffset", offset);
        writer.WriteNumber("byteLength", length);
        writer.WriteNumber("target", 34962);
        writer.WriteEndObject();
    }

    private static byte[] WriteFloats(List<float> values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }
        return bytes;
    }
}

public readonly record struct ModelLocalBounds(
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);

public readonly record struct ObjGlbConversionResult(
    byte[] GlbBytes,
    ModelLocalBounds Bounds,
    int VertexCount,
    int TriangleCount);
