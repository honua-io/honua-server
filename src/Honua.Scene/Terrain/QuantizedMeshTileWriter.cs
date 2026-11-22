// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Scene.Terrain;

public static class QuantizedMeshTileWriter
{
    public const string MediaType = "application/vnd.quantized-mesh";

    public const string OctVertexNormalsExtensionName = "octvertexnormals";

    public const byte OctVertexNormalsExtensionId = 1;

    public const int HeaderByteLength = 88;

    public const ushort MaxQuantizedCoordinate = 32767;

    private const int Index16VertexLimit = 65536;

    public static byte[] Write(QuantizedMeshTileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

        var mesh = BuildMesh(input);
        using var stream = new MemoryStream(EstimateByteLength(mesh));
        WriteHeader(stream, mesh.Header);
        WriteVertexData(stream, mesh.U, mesh.V, mesh.Height);
        WriteIndexData(stream, mesh.Indices, mesh.Uses32BitIndices);
        WriteEdgeIndices(stream, mesh);

        if (mesh.OctEncodedNormals is { Length: > 0 } normals)
        {
            WriteExtension(stream, OctVertexNormalsExtensionId, normals);
        }

        return stream.ToArray();
    }

    public static byte[] WriteGzipped(QuantizedMeshTileInput input)
    {
        var raw = Write(input);
        using var compressed = new MemoryStream(raw.Length / 2);
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw);
        }

        return compressed.ToArray();
    }

    private static void ValidateInput(QuantizedMeshTileInput input)
    {
        if (input.GridSize is < TerrainTmsGrid.MinGridSize or > TerrainTmsGrid.MaxGridSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(input),
                $"GridSize must be between {TerrainTmsGrid.MinGridSize} and {TerrainTmsGrid.MaxGridSize}.");
        }

        var expectedCount = checked(input.GridSize * input.GridSize);
        if (input.Elevations.Length != expectedCount)
        {
            throw new ArgumentException(
                $"Elevations length ({input.Elevations.Length}) must equal GridSize * GridSize ({expectedCount}).",
                nameof(input));
        }
    }

    private static BuiltMesh BuildMesh(QuantizedMeshTileInput input)
    {
        var gridSize = input.GridSize;
        var vertexCount = gridSize * gridSize;
        var (minHeight, maxHeight) = ResolveHeightRange(input.Elevations);
        var (gridU, gridV, gridHeight, gridPositions) = BuildGridVertices(input, minHeight, maxHeight);
        var rawIndices = BuildGridTriangles(gridSize);
        var (remappedIndices, oldToNew, newToOld) = RemapVerticesInVisitOrder(vertexCount, rawIndices);

        var u = Reorder(gridU, newToOld);
        var v = Reorder(gridV, newToOld);
        var height = Reorder(gridHeight, newToOld);
        var positions = Reorder(gridPositions, newToOld);

        var (sphereCenter, sphereRadius) = QuantizedMeshEllipsoid.ComputeBoundingSphere(positions);
        var tileCenter = QuantizedMeshEllipsoid.GeodeticToEcef(
            (input.Bounds.West + input.Bounds.East) * 0.5,
            (input.Bounds.South + input.Bounds.North) * 0.5,
            (minHeight + maxHeight) * 0.5);
        var horizon = QuantizedMeshEllipsoid.ComputeHorizonOcclusionPoint(positions, tileCenter);

        var header = new QuantizedMeshHeader(
            tileCenter.X, tileCenter.Y, tileCenter.Z,
            (float)minHeight, (float)maxHeight,
            sphereCenter.X, sphereCenter.Y, sphereCenter.Z, sphereRadius,
            horizon.X, horizon.Y, horizon.Z);

        var (west, south, east, north) = BuildSortedEdges(gridSize, oldToNew);
        var normals = input.IncludeVertexNormals
            ? QuantizedMeshEllipsoid.ComputeOctEncodedVertexNormals(positions, remappedIndices)
            : null;

        return new BuiltMesh(header, u, v, height, remappedIndices, west, south, east, north, normals, vertexCount > Index16VertexLimit);
    }

    private static (double MinHeight, double MaxHeight) ResolveHeightRange(double?[] elevations)
    {
        var hasFinite = false;
        var minHeight = double.PositiveInfinity;
        var maxHeight = double.NegativeInfinity;

        for (var i = 0; i < elevations.Length; i++)
        {
            var value = elevations[i] is { } h && double.IsFinite(h) ? h : 0.0;
            hasFinite = true;
            minHeight = Math.Min(minHeight, value);
            maxHeight = Math.Max(maxHeight, value);
        }

        return hasFinite ? (minHeight, maxHeight) : (0.0, 0.0);
    }

    private static (ushort[] U, ushort[] V, ushort[] Height, EcefPoint[] Positions) BuildGridVertices(
        QuantizedMeshTileInput input,
        double minHeight,
        double maxHeight)
    {
        var gridSize = input.GridSize;
        var vertexCount = gridSize * gridSize;
        var u = new ushort[vertexCount];
        var v = new ushort[vertexCount];
        var height = new ushort[vertexCount];
        var positions = new EcefPoint[vertexCount];
        var steps = gridSize - 1.0;
        var heightRange = maxHeight - minHeight;

        for (var row = 0; row < gridSize; row++)
        {
            var fracV = row / steps;
            var lat = input.Bounds.South + (fracV * (input.Bounds.North - input.Bounds.South));
            var quantizedV = QuantizeNormalized(fracV);

            for (var col = 0; col < gridSize; col++)
            {
                var idx = (row * gridSize) + col;
                var fracU = col / steps;
                var lon = input.Bounds.West + (fracU * (input.Bounds.East - input.Bounds.West));
                var h = input.Elevations[idx] is { } sample && double.IsFinite(sample) ? sample : 0.0;

                u[idx] = QuantizeNormalized(fracU);
                v[idx] = quantizedV;
                height[idx] = heightRange > 0.0 ? QuantizeNormalized((h - minHeight) / heightRange) : (ushort)0;
                positions[idx] = QuantizedMeshEllipsoid.GeodeticToEcef(lon, lat, h);
            }
        }

        return (u, v, height, positions);
    }

    private static uint[] BuildGridTriangles(int gridSize)
    {
        var cellCount = (gridSize - 1) * (gridSize - 1);
        var indices = new uint[cellCount * 6];
        var cursor = 0;

        for (var row = 0; row < gridSize - 1; row++)
        {
            for (var col = 0; col < gridSize - 1; col++)
            {
                var sw = (uint)((row * gridSize) + col);
                var se = sw + 1;
                var nw = sw + (uint)gridSize;
                var ne = nw + 1;

                indices[cursor++] = sw;
                indices[cursor++] = se;
                indices[cursor++] = ne;
                indices[cursor++] = sw;
                indices[cursor++] = ne;
                indices[cursor++] = nw;
            }
        }

        return indices;
    }

    private static (uint[] RemappedIndices, uint[] OldToNew, int[] NewToOld) RemapVerticesInVisitOrder(
        int vertexCount,
        uint[] rawIndices)
    {
        var oldToNew = new uint[vertexCount];
        var newToOld = new int[vertexCount];
        Array.Fill(oldToNew, uint.MaxValue);

        var remapped = new uint[rawIndices.Length];
        uint nextIndex = 0;

        for (var i = 0; i < rawIndices.Length; i++)
        {
            var oldIdx = (int)rawIndices[i];
            var mapped = oldToNew[oldIdx];
            if (mapped == uint.MaxValue)
            {
                mapped = nextIndex++;
                oldToNew[oldIdx] = mapped;
                newToOld[mapped] = oldIdx;
            }

            remapped[i] = mapped;
        }

        return (remapped, oldToNew, newToOld);
    }

    private static (uint[] West, uint[] South, uint[] East, uint[] North) BuildSortedEdges(int gridSize, uint[] oldToNew)
    {
        var west = new uint[gridSize];
        var south = new uint[gridSize];
        var east = new uint[gridSize];
        var north = new uint[gridSize];
        var last = gridSize - 1;

        for (var i = 0; i < gridSize; i++)
        {
            west[i] = oldToNew[(last - i) * gridSize];
            south[i] = oldToNew[i];
            east[i] = oldToNew[(i * gridSize) + last];
            north[i] = oldToNew[(last * gridSize) + (last - i)];
        }

        return (west, south, east, north);
    }

    private static void WriteHeader(Stream stream, QuantizedMeshHeader header)
    {
        Span<byte> buffer = stackalloc byte[HeaderByteLength];
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(0, 8), header.CenterX);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(8, 8), header.CenterY);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(16, 8), header.CenterZ);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(24, 4), header.MinimumHeight);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(28, 4), header.MaximumHeight);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(32, 8), header.BoundingSphereCenterX);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(40, 8), header.BoundingSphereCenterY);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(48, 8), header.BoundingSphereCenterZ);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(56, 8), header.BoundingSphereRadius);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(64, 8), header.HorizonOcclusionPointX);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(72, 8), header.HorizonOcclusionPointY);
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(80, 8), header.HorizonOcclusionPointZ);
        stream.Write(buffer);
    }

    private static void WriteVertexData(Stream stream, ushort[] u, ushort[] v, ushort[] height)
    {
        WriteUInt32(stream, (uint)u.Length);
        WriteDeltaZigZagArray(stream, u);
        WriteDeltaZigZagArray(stream, v);
        WriteDeltaZigZagArray(stream, height);
    }

    private static void WriteDeltaZigZagArray(Stream stream, ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        var previous = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var delta = values[i] - previous;
            previous = values[i];
            var encoded = (ushort)((delta >> 31) ^ (delta << 1));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), encoded);
        }

        stream.Write(bytes);
    }

    private static void WriteIndexData(Stream stream, uint[] indices, bool use32Bit)
    {
        AlignStream(stream, use32Bit ? 4 : 2);
        WriteUInt32(stream, (uint)(indices.Length / 3));

        var stride = use32Bit ? 4 : 2;
        var bytes = new byte[indices.Length * stride];
        uint highest = 0;

        for (var i = 0; i < indices.Length; i++)
        {
            var code = highest - indices[i];
            if (use32Bit)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), code);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)code);
            }

            if (code == 0)
            {
                highest++;
            }
        }

        stream.Write(bytes);
    }

    private static void WriteEdgeIndices(Stream stream, BuiltMesh mesh)
    {
        WriteIndexList(stream, mesh.WestIndices, mesh.Uses32BitIndices);
        WriteIndexList(stream, mesh.SouthIndices, mesh.Uses32BitIndices);
        WriteIndexList(stream, mesh.EastIndices, mesh.Uses32BitIndices);
        WriteIndexList(stream, mesh.NorthIndices, mesh.Uses32BitIndices);
    }

    private static void WriteIndexList(Stream stream, uint[] indices, bool use32Bit)
    {
        WriteUInt32(stream, (uint)indices.Length);
        var stride = use32Bit ? 4 : 2;
        var bytes = new byte[indices.Length * stride];
        for (var i = 0; i < indices.Length; i++)
        {
            if (use32Bit)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), indices[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)indices[i]);
            }
        }

        stream.Write(bytes);
    }

    private static void WriteExtension(Stream stream, byte extensionId, byte[] payload)
    {
        stream.WriteByte(extensionId);
        WriteUInt32(stream, (uint)payload.Length);
        stream.Write(payload);
    }

    private static void AlignStream(Stream stream, int alignment)
    {
        var remainder = (int)(stream.Position % alignment);
        if (remainder == 0)
        {
            return;
        }

        Span<byte> padding = stackalloc byte[4];
        stream.Write(padding[..(alignment - remainder)]);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static ushort QuantizeNormalized(double normalized)
        => (ushort)Math.Clamp((int)Math.Round(Math.Clamp(normalized, 0.0, 1.0) * MaxQuantizedCoordinate, MidpointRounding.AwayFromZero), 0, MaxQuantizedCoordinate);

    private static T[] Reorder<T>(T[] source, int[] newToOld)
    {
        var result = new T[newToOld.Length];
        for (var i = 0; i < newToOld.Length; i++)
        {
            result[i] = source[newToOld[i]];
        }

        return result;
    }

    private static int EstimateByteLength(BuiltMesh mesh)
    {
        var indexBytes = mesh.Uses32BitIndices ? 4 : 2;
        var edges = mesh.WestIndices.Length + mesh.SouthIndices.Length + mesh.EastIndices.Length + mesh.NorthIndices.Length;
        var extensionBytes = mesh.OctEncodedNormals is { Length: > 0 } n ? 5 + n.Length : 0;
        return HeaderByteLength + 4 + (mesh.U.Length * 6) + 8 + (mesh.Indices.Length * indexBytes) + 16 + (edges * indexBytes) + extensionBytes;
    }

    private sealed record BuiltMesh(
        QuantizedMeshHeader Header,
        ushort[] U,
        ushort[] V,
        ushort[] Height,
        uint[] Indices,
        uint[] WestIndices,
        uint[] SouthIndices,
        uint[] EastIndices,
        uint[] NorthIndices,
        byte[]? OctEncodedNormals,
        bool Uses32BitIndices);
}
