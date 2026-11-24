// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

public sealed record ZarrSubsetReadLimits(
    long MaxOutputBytes = ZarrSubsetReader.DefaultMaxOutputBytes,
    long MaxChunkBytes = ZarrSubsetReader.DefaultMaxChunkBytes,
    long MaxWorkingSetBytes = ZarrSubsetReader.DefaultMaxWorkingSetBytes);

public sealed class ZarrSubsetReader
{
    public const long DefaultMaxOutputBytes = 256L * 1024L * 1024L; // 256 MiB
    public const long DefaultMaxChunkBytes = 32L * 1024L * 1024L; // 32 MiB
    public const long DefaultMaxWorkingSetBytes = 64L * 1024L * 1024L; // 64 MiB

    private const int MaxChunksPerRequest = 4096;
    private const int MaxParallelChunkReads = 8;
    private const int StackAllocMaxRank = 16;
    // DEFLATE may expand incompressible input slightly beyond the decoded chunk size.
    private const long MaxCompressedChunkOverheadBytes = 1L * 1024L * 1024L;

    private static readonly ZarrSubsetReadLimits DefaultLimits = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Retains the injected instance API after removing its single-implementation interface.")]
    public Task<ZarrSubsetResult> ReadSubsetAsync(
        ICloudRangeReader reader,
        string bucket,
        string rootPath,
        ZarrStoreMetadata metadata,
        ZarrSubsetRequest request,
        CancellationToken cancellationToken = default)
        => ReadSubsetAsync(reader, bucket, rootPath, metadata, request, DefaultLimits, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Retains the injected instance API after removing its single-implementation interface.")]
    public async Task<ZarrSubsetResult> ReadSubsetAsync(
        ICloudRangeReader reader,
        string bucket,
        string rootPath,
        ZarrStoreMetadata metadata,
        ZarrSubsetRequest request,
        ZarrSubsetReadLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);

        var maxOutputBytes = Math.Clamp(limits.MaxOutputBytes, 1L, DefaultMaxOutputBytes);
        var maxChunkBytes = Math.Clamp(limits.MaxChunkBytes, 1L, DefaultMaxOutputBytes);
        var maxWorkingSetBytes = Math.Clamp(limits.MaxWorkingSetBytes, 1L, DefaultMaxOutputBytes);

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
        if (totalBytes > maxOutputBytes)
        {
            throw new ZarrSubsetValidationException(
                $"Zarr subset would require {totalBytes:N0} bytes, exceeding the {maxOutputBytes:N0} byte cap.");
        }

        var chunkBytes = ComputeChunkBytes(array, elementSize);
        if (chunkBytes > maxChunkBytes)
        {
            throw new ZarrSubsetValidationException(
                $"Zarr chunk size of {chunkBytes:N0} bytes exceeds the per-chunk cap of {maxChunkBytes:N0} bytes. " +
                $"Re-chunk the Zarr store with smaller chunk dimensions before serving it.");
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
                throw new ZarrSubsetValidationException(
                    $"Zarr subset would span more than {MaxChunksPerRequest} chunks; reduce the request bounds.");
            }
        }

        // Throttle concurrent chunk decodes so peak compressed + decoded buffers stay within the working-set budget.
        var compressedOverhead = array.Compressor is null ? 0L : Math.Min(chunkBytes, MaxCompressedChunkOverheadBytes);
        var perWorkerBytes = checked((chunkBytes * 2L) + compressedOverhead);
        var maxParallelism = (int)Math.Clamp(
            maxWorkingSetBytes / Math.Max(1L, perWorkerBytes),
            1L,
            MaxParallelChunkReads);

        var output = new byte[totalBytes];
        var arrayPath = JoinKey(rootPath, array.RelativePath);

        // Enumerate every required chunk index upfront, then dispatch the cloud reads with
        // bounded parallelism. Each
        // chunk's copy targets a disjoint region of the grid, so concurrent writes into
        // `output` never overlap and require no synchronization.
        var chunkIndices = new List<int[]>();
        var cursor = new int[rank];
        Array.Copy(startChunk, cursor, rank);
        var enumerating = true;
        while (enumerating)
        {
            chunkIndices.Add((int[])cursor.Clone());
            enumerating = TryIncrement(cursor, startChunk, stopChunk);
        }

        await Parallel.ForEachAsync(
            chunkIndices,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = cancellationToken },
            (chunkIndex, ct) => new ValueTask(CopyChunkIntoSubsetAsync(
                reader,
                bucket,
                arrayPath,
                array,
                request,
                subsetShape,
                elementSize,
                chunkBytes,
                chunkIndex,
                output,
                ct)))
            .ConfigureAwait(false);

        return new ZarrSubsetResult(
            Variable: array.Name,
            Shape: subsetShape,
            DataType: array.DataType,
            Data: output);
    }

    private static ZarrArrayMetadata ResolveArray(ZarrStoreMetadata metadata, string variable)
    {
        return metadata.Arrays.FirstOrDefault(candidate => string.Equals(candidate.Name, variable, StringComparison.Ordinal))
            ?? throw new ZarrSubsetValidationException($"Variable '{variable}' was not discovered in the Zarr store.");
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

        if (string.Equals(compressor, "zlib", StringComparison.Ordinal) ||
            string.Equals(compressor, "gzip", StringComparison.Ordinal))
        {
            return;
        }

        throw new ZarrSubsetValidationException(
            $"Zarr compressor '{compressor}' is not supported by the MVP reader. Use uncompressed, zlib, or gzip chunks.");
    }

    internal static int ResolveElementSize(string dtype)
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
            throw new ZarrSubsetValidationException($"Zarr dtype '{dtype}' is big-endian; only little-endian dtypes are supported by the MVP reader.");
        }

        if (span.Length < 3)
        {
            throw new InvalidDataException($"Zarr dtype '{dtype}' is too short to interpret.");
        }

        var kind = span[1];
        if (kind is not ('f' or 'i' or 'u' or 'b'))
        {
            throw new ZarrSubsetValidationException($"Zarr dtype kind '{kind}' is not supported by the MVP reader (use f, i, u, or b).");
        }

        if (!int.TryParse(span[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes <= 0)
        {
            throw new InvalidDataException($"Zarr dtype '{dtype}' has an invalid element size suffix.");
        }

        // All supported numeric dtypes fit within 16 bytes (f16 / complex128 is the largest).
        // Reject unrealistic values early to prevent oversized allocation.
        const int MaxSupportedElementBytes = 16;
        if (bytes > MaxSupportedElementBytes)
        {
            throw new InvalidDataException(
                $"Zarr dtype '{dtype}' has an element size of {bytes} bytes, which exceeds the maximum of {MaxSupportedElementBytes}.");
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
        long chunkBytes,
        int[] chunkIndex,
        byte[] output,
        CancellationToken cancellationToken)
    {
        var chunkPath = JoinKey(arrayPath, BuildChunkKey(chunkIndex, array.ChunkKeyEncoding));

        byte[]? raw;
        try
        {
            var objectSize = await reader.GetObjectSizeAsync(bucket, chunkPath, cancellationToken).ConfigureAwait(false);
            if (objectSize <= 0)
            {
                throw new InvalidDataException("An existing Zarr chunk object must contain its encoded payload.");
            }
            else
            {
                // Uncompressed chunks are bounded by their decoded size. Compressed
                // chunks allow a bounded overhead above chunkBytes for incompressible
                // DEFLATE streams rather than permitting multi-hundred-MiB reads.
                var readLimit = array.Compressor is null
                    ? chunkBytes
                    : checked(chunkBytes + MaxCompressedChunkOverheadBytes);
                if (objectSize > readLimit || array.Compressor is null && objectSize != chunkBytes)
                    throw new InvalidDataException("Zarr chunk object length exceeds its encoded limit or does not match its declared shape.");
                raw = await reader.ReadRangeAsync(
                        bucket, chunkPath, 0, checked((int)objectSize), cancellationToken)
                    .ConfigureAwait(false);
                if (raw.LongLength != objectSize)
                    throw new InvalidDataException("Zarr chunk range read did not return the complete encoded object.");
            }
        }
        catch (FileNotFoundException)
        {
            raw = null;
        }
        catch (DirectoryNotFoundException)
        {
            raw = null;
        }

        var chunkByteCount = checked((int)chunkBytes);
        var chunkBuffer = ArrayPool<byte>.Shared.Rent(chunkByteCount);
        try
        {
            if (raw is null)
            {
                if (array.FillValue is null)
                {
                    chunkBuffer.AsSpan(0, chunkByteCount).Clear();
                }
                else
                {
                    FillWithFillValue(chunkBuffer.AsSpan(0, chunkByteCount), array, elementSize, cancellationToken);
                }
            }
            else
            {
                ZarrChunkDecoder.Decode(raw, array.Compressor, chunkBuffer, chunkByteCount, cancellationToken);
            }

            CopyChunkRegion(chunkBuffer, output, array, request, subsetShape, chunkIndex, elementSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    private static void CopyChunkRegion(
        byte[] chunkBuffer,
        byte[] output,
        ZarrArrayMetadata array,
        ZarrSubsetRequest request,
        int[] subsetShape,
        int[] chunkIndex,
        int elementSize)
    {
        var rank = array.Shape.Length;
        if (rank == 0)
        {
            Buffer.BlockCopy(chunkBuffer, 0, output, 0, elementSize);
            return;
        }

        // For each cell in the chunk intersected with the request bounds, copy
        // it to the appropriate output offset using stack-allocated coordinate and stride vectors.
        Span<int> chunkStart = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank];
        Span<int> intersectStart = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank];
        Span<int> intersectStop = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank];
        Span<int> cursor = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank];
        Span<long> chunkStrides = rank <= StackAllocMaxRank ? stackalloc long[rank] : new long[rank];
        Span<long> subsetStrides = rank <= StackAllocMaxRank ? stackalloc long[rank] : new long[rank];

        for (var i = 0; i < rank; i++)
        {
            var cStart = chunkIndex[i] * array.Chunks[i];
            var cStop = Math.Min(cStart + array.Chunks[i], array.Shape[i]);
            chunkStart[i] = cStart;

            var iStart = Math.Max(cStart, request.Start[i]);
            var iStop = Math.Min(cStop, request.Stop[i]);
            if (iStop <= iStart)
            {
                return;
            }

            intersectStart[i] = iStart;
            intersectStop[i] = iStop;
            cursor[i] = iStart;
        }

        long chunkStride = 1;
        long subsetStride = 1;
        for (var i = rank - 1; i >= 0; i--)
        {
            chunkStrides[i] = chunkStride;
            subsetStrides[i] = subsetStride;
            chunkStride *= array.Chunks[i];
            subsetStride *= subsetShape[i];
        }

        // Iterate over all cells in the intersected region using row-major coordinates
        // and copy contiguous innermost runs at a time.
        var innerStart = intersectStart[rank - 1];
        var innerStop = intersectStop[rank - 1];
        var runBytes = checked((innerStop - innerStart) * elementSize);

        while (true)
        {
            cursor[rank - 1] = innerStart;

            var srcOffset = checked((int)(ComputeStridedOffset(cursor, chunkStart, chunkStrides) * elementSize));
            var dstOffset = checked((int)(ComputeStridedOffset(cursor, request.Start, subsetStrides) * elementSize));
            Buffer.BlockCopy(chunkBuffer, srcOffset, output, dstOffset, runBytes);

            if (!TryIncrementOuter(cursor, intersectStart, intersectStop))
            {
                break;
            }
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

    private static void FillWithFillValue(Span<byte> buffer, ZarrArrayMetadata array, int elementSize, CancellationToken cancellationToken)
        => ZarrFillValueCodec.Fill(buffer, array.DataType, elementSize, array.FillValue, cancellationToken);

    private static long ComputeStridedOffset(
        ReadOnlySpan<int> cell,
        ReadOnlySpan<int> origin,
        ReadOnlySpan<long> strides)
    {
        long offset = 0;
        for (var i = 0; i < cell.Length; i++)
        {
            offset += (cell[i] - origin[i]) * strides[i];
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

    private static bool TryIncrementOuter(Span<int> cursor, ReadOnlySpan<int> start, ReadOnlySpan<int> stop)
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

    private static string BuildChunkKey(int[] chunkIndex, ZarrChunkKeyEncoding encoding)
    {
        // v2 dotted form ("0.1"); v3 default form is "c"-prefixed and slash-separated ("c/0/1").
        if (chunkIndex.Length == 0)
        {
            return encoding.RankZeroKey;
        }
        var sb = new System.Text.StringBuilder();
        if (encoding.Prefix.Length > 0)
        {
            sb.Append(encoding.Prefix).Append(encoding.Separator);
        }
        for (var i = 0; i < chunkIndex.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(encoding.Separator);
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
