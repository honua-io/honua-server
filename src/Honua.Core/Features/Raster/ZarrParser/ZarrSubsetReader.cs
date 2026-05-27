// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO.Compression;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// AOT-safe Zarr v2 subset reader. Supports the uncompressed and zlib codecs,
/// C order, and per-dimension bounded reads. The result buffer is laid out in
/// row-major order over the requested subset shape using the array's native dtype.
/// </summary>
public sealed class ZarrSubsetReader : IZarrSubsetReader
{
    private const int MaxChunksPerRequest = 4096;
    private const long MaxBytesPerRequest = 256L * 1024L * 1024L; // 256 MiB

    /// <inheritdoc />
    public async Task<ZarrSubsetResult> ReadSubsetAsync(
        ICloudRangeReader reader,
        string bucket,
        string rootPath,
        ZarrStoreMetadata metadata,
        ZarrSubsetRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(request);

        var array = ResolveArray(metadata, request.Variable);
        ValidateRequest(array, request);
        ValidateCodec(array);

        var rank = array.Shape.Length;
        var subsetShape = new int[rank];
        for (var i = 0; i < rank; i++)
        {
            subsetShape[i] = request.Stop[i] - request.Start[i];
        }

        var elementSize = ResolveElementSize(array.DataType);
        var totalElements = 1L;
        for (var i = 0; i < rank; i++)
        {
            totalElements *= subsetShape[i];
        }

        var totalBytes = checked(totalElements * elementSize);
        if (totalBytes > MaxBytesPerRequest)
        {
            throw new InvalidOperationException(
                $"Zarr subset would require {totalBytes:N0} bytes, exceeding the {MaxBytesPerRequest:N0} byte cap.");
        }

        var startChunk = new int[rank];
        var stopChunk = new int[rank];
        var chunkCount = 1L;
        for (var i = 0; i < rank; i++)
        {
            startChunk[i] = request.Start[i] / array.Chunks[i];
            stopChunk[i] = (request.Stop[i] - 1) / array.Chunks[i] + 1;
            chunkCount = checked(chunkCount * (stopChunk[i] - startChunk[i]));
            if (chunkCount > MaxChunksPerRequest)
            {
                throw new InvalidOperationException(
                    $"Zarr subset would span more than {MaxChunksPerRequest} chunks; reduce the request bounds.");
            }
        }

        var output = new byte[totalBytes];
        var chunkIndex = new int[rank];
        Array.Copy(startChunk, chunkIndex, rank);

        var arrayPath = JoinKey(rootPath, array.RelativePath);

        var done = false;
        while (!done)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await CopyChunkIntoSubsetAsync(
                    reader,
                    bucket,
                    arrayPath,
                    array,
                    request,
                    subsetShape,
                    elementSize,
                    chunkIndex,
                    output,
                    cancellationToken)
                .ConfigureAwait(false);

            // Increment chunkIndex within the [startChunk, stopChunk) range, row-major.
            done = !TryIncrement(chunkIndex, startChunk, stopChunk);
        }

        return new ZarrSubsetResult(
            Variable: array.Name,
            Shape: subsetShape,
            DataType: array.DataType,
            Data: output);
    }

    private static ZarrArrayMetadata ResolveArray(ZarrStoreMetadata metadata, string variable)
    {
        foreach (var candidate in metadata.Arrays)
        {
            if (string.Equals(candidate.Name, variable, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException($"Variable '{variable}' was not discovered in the Zarr store.");
    }

    private static void ValidateRequest(ZarrArrayMetadata array, ZarrSubsetRequest request)
    {
        if (request.Start.Length != array.Shape.Length || request.Stop.Length != array.Shape.Length)
        {
            throw new ArgumentException(
                $"Zarr subset rank mismatch: array '{array.Name}' has rank {array.Shape.Length}.",
                nameof(request));
        }

        for (var i = 0; i < array.Shape.Length; i++)
        {
            if (request.Start[i] < 0 || request.Stop[i] <= request.Start[i] || request.Stop[i] > array.Shape[i])
            {
                throw new ArgumentException(
                    $"Zarr subset bounds [{request.Start[i]},{request.Stop[i]}) on dimension {i} are outside the array shape {array.Shape[i]}.",
                    nameof(request));
            }
        }
    }

    private static void ValidateCodec(ZarrArrayMetadata array)
    {
        var compressor = array.Compressor;
        if (compressor is null)
        {
            return;
        }

        if (string.Equals(compressor, "zlib", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Zarr compressor '{compressor}' is not supported by the MVP reader. Use uncompressed or zlib chunks.");
    }

    private static int ResolveElementSize(string dtype)
    {
        var span = dtype.AsSpan();
        if (span.IsEmpty)
        {
            throw new InvalidDataException("Zarr dtype is empty.");
        }

        // dtype follows numpy convention: [<|>|=][kind][bytes], e.g. <f4, |u1, >i8.
        var endian = span[0];
        if (endian is not ('<' or '>' or '|' or '='))
        {
            throw new InvalidDataException($"Zarr dtype '{dtype}' has unrecognized byte-order prefix.");
        }
        if (endian == '>')
        {
            throw new InvalidOperationException($"Zarr dtype '{dtype}' is big-endian; only little-endian dtypes are supported by the MVP reader.");
        }

        if (span.Length < 3)
        {
            throw new InvalidDataException($"Zarr dtype '{dtype}' is too short to interpret.");
        }

        var kind = span[1];
        if (kind is not ('f' or 'i' or 'u' or 'b'))
        {
            throw new InvalidOperationException($"Zarr dtype kind '{kind}' is not supported by the MVP reader (use f, i, u, or b).");
        }

        if (!int.TryParse(span[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
        {
            throw new InvalidDataException($"Zarr dtype '{dtype}' has an invalid element size suffix.");
        }
        return bytes;
    }

    private static async Task CopyChunkIntoSubsetAsync(
        ICloudRangeReader reader,
        string bucket,
        string arrayPath,
        ZarrArrayMetadata array,
        ZarrSubsetRequest request,
        int[] subsetShape,
        int elementSize,
        int[] chunkIndex,
        byte[] output,
        CancellationToken cancellationToken)
    {
        var rank = array.Shape.Length;
        var chunkPath = JoinKey(arrayPath, BuildChunkKey(chunkIndex));
        byte[]? raw;
        try
        {
            raw = await reader.ReadRangeAsync(bucket, chunkPath, 0, int.MaxValue / 4, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            raw = null;
        }
        catch (DirectoryNotFoundException)
        {
            raw = null;
        }

        var chunkBytes = ComputeChunkBytes(array, elementSize);
        var chunkBuffer = new byte[chunkBytes];
        if (raw is null || raw.Length == 0)
        {
            FillWithFillValue(chunkBuffer, array, elementSize);
        }
        else
        {
            DecompressChunk(raw, array.Compressor, chunkBuffer);
        }

        // For each cell in the chunk intersected with the request bounds, copy
        // it to the appropriate output offset.
        var chunkStart = new int[rank];
        var chunkStop = new int[rank];
        for (var i = 0; i < rank; i++)
        {
            chunkStart[i] = chunkIndex[i] * array.Chunks[i];
            chunkStop[i] = Math.Min(chunkStart[i] + array.Chunks[i], array.Shape[i]);
        }

        var intersectStart = new int[rank];
        var intersectStop = new int[rank];
        for (var i = 0; i < rank; i++)
        {
            intersectStart[i] = Math.Max(chunkStart[i], request.Start[i]);
            intersectStop[i] = Math.Min(chunkStop[i], request.Stop[i]);
            if (intersectStop[i] <= intersectStart[i])
            {
                return;
            }
        }

        // Iterate over all cells in the intersected region using row-major coordinates
        // and copy contiguous innermost runs at a time.
        var cursor = (int[])intersectStart.Clone();
        var done = false;
        while (!done)
        {
            // Copy a run on the last axis from cursor[rank-1] to intersectStop[rank-1].
            var innerStart = intersectStart[rank - 1];
            var innerStop = intersectStop[rank - 1];
            var runLength = innerStop - innerStart;
            cursor[rank - 1] = innerStart;

            var srcOffset = checked((int)(ComputeChunkOffset(cursor, chunkStart, array.Chunks) * elementSize));
            var dstOffset = checked((int)(ComputeSubsetOffset(cursor, request.Start, subsetShape) * elementSize));
            Buffer.BlockCopy(chunkBuffer, srcOffset, output, dstOffset, runLength * elementSize);

            done = !TryIncrementOuter(cursor, intersectStart, intersectStop);
        }
    }

    private static long ComputeChunkBytes(ZarrArrayMetadata array, int elementSize)
    {
        long bytes = elementSize;
        foreach (var c in array.Chunks)
        {
            bytes = checked(bytes * c);
        }
        return bytes;
    }

    private static void FillWithFillValue(byte[] buffer, ZarrArrayMetadata array, int elementSize)
    {
        if (array.FillValue is null)
        {
            return; // zero-filled by default.
        }

        // For MVP we only fill numeric fill values that round-trip to little-endian bytes.
        // String fill values (e.g. NaN encoded as "NaN") are out of scope.
        if (array.FillValue is double d)
        {
            WriteFillScalar(buffer, array.DataType, elementSize, d);
        }
    }

    private static void WriteFillScalar(byte[] buffer, string dtype, int elementSize, double value)
    {
        // Best-effort fill for f4/f8/i*/u*; out-of-range values silently clamp to defaults.
        Span<byte> scratch = stackalloc byte[8];
        switch (dtype)
        {
            case "<f8":
                BitConverter.TryWriteBytes(scratch, value);
                break;
            case "<f4":
                BitConverter.TryWriteBytes(scratch, (float)value);
                break;
            case "<i4":
            case "|i4":
                BitConverter.TryWriteBytes(scratch, (int)value);
                break;
            case "<i2":
            case "|i2":
                BitConverter.TryWriteBytes(scratch, (short)value);
                break;
            case "|i1":
            case "<i1":
                scratch[0] = (byte)(sbyte)value;
                break;
            case "<u4":
            case "|u4":
                BitConverter.TryWriteBytes(scratch, (uint)value);
                break;
            case "<u2":
            case "|u2":
                BitConverter.TryWriteBytes(scratch, (ushort)value);
                break;
            case "|u1":
            case "<u1":
                scratch[0] = (byte)value;
                break;
            default:
                return;
        }

        for (var offset = 0; offset + elementSize <= buffer.Length; offset += elementSize)
        {
            for (var k = 0; k < elementSize; k++)
            {
                buffer[offset + k] = scratch[k];
            }
        }
    }

    private static void DecompressChunk(byte[] raw, string? compressor, byte[] destination)
    {
        if (compressor is null)
        {
            if (raw.Length < destination.Length)
            {
                throw new InvalidDataException(
                    $"Zarr chunk is {raw.Length} bytes but the array expects {destination.Length} uncompressed bytes.");
            }
            Buffer.BlockCopy(raw, 0, destination, 0, destination.Length);
            return;
        }

        if (string.Equals(compressor, "zlib", StringComparison.Ordinal))
        {
            // MemoryStream(byte[]) is a non-copying wrapper; the destination buffer is supplied
            // by the caller (already pool-friendly), so we decompress straight into it via the
            // Span<byte> overload, avoiding any intermediate MemoryStream/ToArray() allocation.
            using var input = new MemoryStream(raw);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var totalRead = 0;
            while (totalRead < destination.Length)
            {
                var read = zlib.Read(destination.AsSpan(totalRead));
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }
            if (totalRead < destination.Length)
            {
                throw new InvalidDataException(
                    $"Zarr zlib chunk decoded to {totalRead} bytes; expected {destination.Length}.");
            }
            return;
        }

        throw new InvalidOperationException($"Zarr compressor '{compressor}' is not supported.");
    }

    private static long ComputeChunkOffset(int[] cell, int[] chunkStart, int[] chunkShape)
    {
        long offset = 0;
        long stride = 1;
        for (var i = cell.Length - 1; i >= 0; i--)
        {
            var local = cell[i] - chunkStart[i];
            offset += local * stride;
            stride *= chunkShape[i];
        }
        return offset;
    }

    private static long ComputeSubsetOffset(int[] cell, int[] requestStart, int[] subsetShape)
    {
        long offset = 0;
        long stride = 1;
        for (var i = cell.Length - 1; i >= 0; i--)
        {
            var local = cell[i] - requestStart[i];
            offset += local * stride;
            stride *= subsetShape[i];
        }
        return offset;
    }

    private static bool TryIncrement(int[] cursor, int[] start, int[] stop)
    {
        for (var i = cursor.Length - 1; i >= 0; i--)
        {
            cursor[i]++;
            if (cursor[i] < stop[i])
            {
                return true;
            }
            cursor[i] = start[i];
        }
        return false;
    }

    private static bool TryIncrementOuter(int[] cursor, int[] start, int[] stop)
    {
        // Increment all axes except the innermost (which the caller handled via run length).
        for (var i = cursor.Length - 2; i >= 0; i--)
        {
            cursor[i]++;
            if (cursor[i] < stop[i])
            {
                return true;
            }
            cursor[i] = start[i];
        }
        return false;
    }

    private static string BuildChunkKey(int[] chunkIndex)
    {
        // Zarr v2 default separator is '.'.
        if (chunkIndex.Length == 0)
        {
            return "0";
        }
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < chunkIndex.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('.');
            }
            sb.Append(chunkIndex[i].ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string JoinKey(string root, string segment)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return root;
        }
        if (string.IsNullOrEmpty(root))
        {
            return segment;
        }
        return root + "/" + segment;
    }

}
