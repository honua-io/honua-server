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

public sealed record ZarrSubsetReadParameters
{
    public required ICloudRangeReader Reader { get; init; }

    public required string Bucket { get; init; }

    public required string RootPath { get; init; }

    public required ZarrStoreMetadata Metadata { get; init; }

    public required ZarrSubsetRequest Request { get; init; }

    public ZarrSubsetReadLimits? Limits { get; init; }
}

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
    public async Task<ZarrSubsetResult> ReadSubsetAsync(
        ZarrSubsetReadParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var plan = PrepareReadPlan(parameters);
        var chunkIndices = EnumerateChunkIndices(plan.StartChunk, plan.StopChunk);

        // Each chunk copy writes a disjoint region of plan.CopyContext.Output, so concurrent writes require no lock.
        await Parallel.ForEachAsync(
            chunkIndices,
            new ParallelOptions { MaxDegreeOfParallelism = plan.MaxParallelism, CancellationToken = cancellationToken },
            (chunkIndex, ct) => new ValueTask(CopyChunkIntoSubsetAsync(plan.CopyContext, chunkIndex, ct)))
            .ConfigureAwait(false);

        return new ZarrSubsetResult(
            Variable: plan.CopyContext.Array.Name,
            Shape: plan.CopyContext.SubsetShape,
            DataType: plan.CopyContext.Array.DataType,
            Data: plan.CopyContext.Output);
    }

    private static SubsetReadPlan PrepareReadPlan(ZarrSubsetReadParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters.Reader);
        ArgumentNullException.ThrowIfNull(parameters.Metadata);
        ArgumentNullException.ThrowIfNull(parameters.Request);

        var limits = parameters.Limits ?? DefaultLimits;
        var array = ResolveArray(parameters.Metadata, parameters.Request.Variable);
        ValidateRequest(array, parameters.Request);
        ValidateCodec(array);

        var dimensions = ComputeSubsetDimensions(array, parameters.Request, limits);
        var (startChunk, stopChunk) = ComputeChunkBounds(array, parameters.Request);
        var maxWorkingSetBytes = Math.Clamp(limits.MaxWorkingSetBytes, 1L, DefaultMaxOutputBytes);
        var maxParallelism = ComputeMaxParallelism(array, dimensions.ChunkBytes, maxWorkingSetBytes);

        return new SubsetReadPlan
        {
            StartChunk = startChunk,
            StopChunk = stopChunk,
            MaxParallelism = maxParallelism,
            CopyContext = new ChunkCopyContext
            {
                Reader = parameters.Reader,
                Bucket = parameters.Bucket,
                ArrayPath = JoinKey(parameters.RootPath, array.RelativePath),
                Array = array,
                Request = parameters.Request,
                SubsetShape = dimensions.SubsetShape,
                ElementSize = dimensions.ElementSize,
                ChunkBytes = dimensions.ChunkBytes,
                Output = new byte[dimensions.TotalBytes],
            },
        };
    }

    private static SubsetDimensions ComputeSubsetDimensions(
        ZarrArrayMetadata array,
        ZarrSubsetRequest request,
        ZarrSubsetReadLimits limits)
    {
        var maxOutputBytes = Math.Clamp(limits.MaxOutputBytes, 1L, DefaultMaxOutputBytes);
        var maxChunkBytes = Math.Clamp(limits.MaxChunkBytes, 1L, DefaultMaxOutputBytes);
        var (subsetShape, totalElements) = ComputeSubsetShape(array.Shape.Length, request);
        var elementSize = ResolveElementSize(array.DataType);
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

        return new SubsetDimensions
        {
            SubsetShape = subsetShape,
            ElementSize = elementSize,
            TotalBytes = totalBytes,
            ChunkBytes = chunkBytes,
        };
    }

    private static (int[] SubsetShape, long TotalElements) ComputeSubsetShape(int rank, ZarrSubsetRequest request)
    {
        var subsetShape = new int[rank];
        var totalElements = 1L;
        for (var i = 0; i < rank; i++)
        {
            subsetShape[i] = request.Stop[i] - request.Start[i];
            totalElements *= subsetShape[i];
        }

        return (subsetShape, totalElements);
    }

    private static (int[] StartChunk, int[] StopChunk) ComputeChunkBounds(
        ZarrArrayMetadata array,
        ZarrSubsetRequest request)
    {
        var rank = array.Shape.Length;
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

        return (startChunk, stopChunk);
    }

    private static int ComputeMaxParallelism(ZarrArrayMetadata array, long chunkBytes, long maxWorkingSetBytes)
    {
        // Throttle concurrent chunk decodes so peak compressed + decoded buffers stay within the working-set budget.
        var compressedOverhead = array.Compressor is null ? 0L : Math.Min(chunkBytes, MaxCompressedChunkOverheadBytes);
        var perWorkerBytes = checked((chunkBytes * 2L) + compressedOverhead);
        return (int)Math.Clamp(
            maxWorkingSetBytes / Math.Max(1L, perWorkerBytes),
            1L,
            MaxParallelChunkReads);
    }

    private static List<int[]> EnumerateChunkIndices(int[] startChunk, int[] stopChunk)
    {
        var chunkIndices = new List<int[]>();
        var cursor = new int[startChunk.Length];
        Array.Copy(startChunk, cursor, startChunk.Length);
        var enumerating = true;
        while (enumerating)
        {
            chunkIndices.Add((int[])cursor.Clone());
            enumerating = TryIncrement(cursor, startChunk, stopChunk);
        }

        return chunkIndices;
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
        ChunkCopyContext context,
        int[] chunkIndex,
        CancellationToken cancellationToken)
    {
        var chunkPath = JoinKey(context.ArrayPath, BuildChunkKey(chunkIndex, context.Array.ChunkKeyEncoding));
        var raw = await ReadEncodedChunkAsync(context, chunkPath, cancellationToken).ConfigureAwait(false);

        var chunkByteCount = checked((int)context.ChunkBytes);
        var chunkBuffer = ArrayPool<byte>.Shared.Rent(chunkByteCount);
        try
        {
            PopulateChunkBuffer(
                context,
                new DecodedChunkTarget { Raw = raw, Buffer = chunkBuffer, ByteCount = chunkByteCount },
                cancellationToken);
            CopyChunkRegion(context, chunkBuffer, chunkIndex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuffer);
        }
    }

    private static void PopulateChunkBuffer(
        ChunkCopyContext context,
        DecodedChunkTarget target,
        CancellationToken cancellationToken)
    {
        if (target.Raw is null)
        {
            if (context.Array.FillValue is null)
            {
                target.Buffer.AsSpan(0, target.ByteCount).Clear();
            }
            else
            {
                ZarrFillValueCodec.Fill(
                    target.Buffer.AsSpan(0, target.ByteCount),
                    context.Array.DataType,
                    context.ElementSize,
                    context.Array.FillValue,
                    cancellationToken);
            }

            return;
        }

        ZarrChunkDecoder.Decode(
            new ZarrChunkDecodeRequest
            {
                Raw = target.Raw,
                Compressor = context.Array.Compressor,
                Destination = target.Buffer,
                DestinationLength = target.ByteCount,
            },
            cancellationToken);
    }

    private static async Task<byte[]?> ReadEncodedChunkAsync(
        ChunkCopyContext context,
        string chunkPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var objectSize = await context.Reader
                .GetObjectSizeAsync(context.Bucket, chunkPath, cancellationToken)
                .ConfigureAwait(false);
            ValidateChunkObjectSize(context, objectSize);

            var raw = await context.Reader
                .ReadRangeAsync(context.Bucket, chunkPath, 0, checked((int)objectSize), cancellationToken)
                .ConfigureAwait(false);
            if (raw.LongLength != objectSize)
            {
                throw new InvalidDataException("Zarr chunk range read did not return the complete encoded object.");
            }

            return raw;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void ValidateChunkObjectSize(ChunkCopyContext context, long objectSize)
    {
        if (objectSize <= 0)
        {
            throw new InvalidDataException("An existing Zarr chunk object must contain its encoded payload.");
        }

        // Uncompressed chunks are bounded by their decoded size. Compressed chunks allow a bounded overhead.
        var readLimit = context.Array.Compressor is null
            ? context.ChunkBytes
            : checked(context.ChunkBytes + MaxCompressedChunkOverheadBytes);
        if (objectSize > readLimit || (context.Array.Compressor is null && objectSize != context.ChunkBytes))
        {
            throw new InvalidDataException("Zarr chunk object length exceeds its encoded limit or does not match its declared shape.");
        }
    }

    private static void CopyChunkRegion(ChunkCopyContext context, byte[] chunkBuffer, int[] chunkIndex)
    {
        var rank = context.Array.Shape.Length;
        if (rank == 0)
        {
            Buffer.BlockCopy(chunkBuffer, 0, context.Output, 0, context.ElementSize);
            return;
        }

        // Keep coordinate and stride vectors on the stack for typical ranks.
        var buffers = new ChunkRegionBuffers
        {
            ChunkStart = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank],
            IntersectStart = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank],
            IntersectStop = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank],
            Cursor = rank <= StackAllocMaxRank ? stackalloc int[rank] : new int[rank],
            ChunkStrides = rank <= StackAllocMaxRank ? stackalloc long[rank] : new long[rank],
            SubsetStrides = rank <= StackAllocMaxRank ? stackalloc long[rank] : new long[rank],
        };

        if (!TryPopulateIntersection(context, chunkIndex, buffers))
        {
            return;
        }

        PopulateStrides(context.Array.Chunks, context.SubsetShape, buffers);
        CopyIntersectedRuns(context, chunkBuffer, buffers);
    }

    private static bool TryPopulateIntersection(
        ChunkCopyContext context,
        int[] chunkIndex,
        ChunkRegionBuffers buffers)
    {
        var rank = context.Array.Shape.Length;
        for (var i = 0; i < rank; i++)
        {
            var cStart = chunkIndex[i] * context.Array.Chunks[i];
            var cStop = Math.Min(cStart + context.Array.Chunks[i], context.Array.Shape[i]);
            buffers.ChunkStart[i] = cStart;

            var iStart = Math.Max(cStart, context.Request.Start[i]);
            var iStop = Math.Min(cStop, context.Request.Stop[i]);
            if (iStop <= iStart)
            {
                return false;
            }

            buffers.IntersectStart[i] = iStart;
            buffers.IntersectStop[i] = iStop;
            buffers.Cursor[i] = iStart;
        }

        return true;
    }

    private static void PopulateStrides(int[] chunks, int[] subsetShape, ChunkRegionBuffers buffers)
    {
        long chunkStride = 1;
        long subsetStride = 1;
        for (var i = chunks.Length - 1; i >= 0; i--)
        {
            buffers.ChunkStrides[i] = chunkStride;
            buffers.SubsetStrides[i] = subsetStride;
            chunkStride *= chunks[i];
            subsetStride *= subsetShape[i];
        }
    }

    private static void CopyIntersectedRuns(
        ChunkCopyContext context,
        byte[] chunkBuffer,
        ChunkRegionBuffers buffers)
    {
        var rank = context.Array.Shape.Length;
        var innerStart = buffers.IntersectStart[rank - 1];
        var innerStop = buffers.IntersectStop[rank - 1];
        var runBytes = checked((innerStop - innerStart) * context.ElementSize);

        while (true)
        {
            buffers.Cursor[rank - 1] = innerStart;
            var srcOffset = checked((int)(ComputeStridedOffset(buffers.Cursor, buffers.ChunkStart, buffers.ChunkStrides) * context.ElementSize));
            var dstOffset = checked((int)(ComputeStridedOffset(buffers.Cursor, context.Request.Start, buffers.SubsetStrides) * context.ElementSize));
            Buffer.BlockCopy(chunkBuffer, srcOffset, context.Output, dstOffset, runBytes);

            if (!TryIncrementOuter(buffers.Cursor, buffers.IntersectStart, buffers.IntersectStop))
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

    private sealed class SubsetReadPlan
    {
        public required int[] StartChunk { get; init; }

        public required int[] StopChunk { get; init; }

        public required int MaxParallelism { get; init; }

        public required ChunkCopyContext CopyContext { get; init; }
    }

    private readonly record struct SubsetDimensions
    {
        public required int[] SubsetShape { get; init; }

        public required int ElementSize { get; init; }

        public required long TotalBytes { get; init; }

        public required long ChunkBytes { get; init; }
    }

    private sealed class ChunkCopyContext
    {
        public required ICloudRangeReader Reader { get; init; }

        public required string Bucket { get; init; }

        public required string ArrayPath { get; init; }

        public required ZarrArrayMetadata Array { get; init; }

        public required ZarrSubsetRequest Request { get; init; }

        public required int[] SubsetShape { get; init; }

        public required int ElementSize { get; init; }

        public required long ChunkBytes { get; init; }

        public required byte[] Output { get; init; }
    }

    private readonly record struct DecodedChunkTarget
    {
        public byte[]? Raw { get; init; }

        public required byte[] Buffer { get; init; }

        public required int ByteCount { get; init; }
    }

    private ref struct ChunkRegionBuffers
    {
        public Span<int> ChunkStart;
        public Span<int> IntersectStart;
        public Span<int> IntersectStop;
        public Span<int> Cursor;
        public Span<long> ChunkStrides;
        public Span<long> SubsetStrides;
    }
}
