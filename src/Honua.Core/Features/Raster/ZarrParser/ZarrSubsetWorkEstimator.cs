// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Capacity;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Computes a conservative managed-memory and object-range estimate for a planned
/// Zarr subset without reading an object or allocating a raster-sized buffer.
/// </summary>
public static class ZarrSubsetWorkEstimator
{
    /// <summary>
    /// Estimates synchronous work for a subset rendered into a web raster.
    /// Object bytes conservatively count every intersecting chunk at its declared
    /// uncompressed size; web bytes include decoded subset and RGBA output buffers.
    /// </summary>
    public static RasterCapacityWork Estimate(
        ZarrArrayMetadata array,
        ZarrSubsetRequest request,
        int outputWidth,
        int outputHeight)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(request);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);

        if (request.Start.Length != array.Shape.Length || request.Stop.Length != array.Shape.Length ||
            array.Chunks.Length != array.Shape.Length)
        {
            throw new ArgumentException("Zarr subset and chunk ranks must match the array shape.", nameof(request));
        }

        var elementSize = ZarrSubsetReader.ResolveElementSize(array.DataType);
        long subsetElements = 1;
        long chunkCount = 1;
        long chunkBytes = elementSize;

        for (var i = 0; i < array.Shape.Length; i++)
        {
            var start = request.Start[i];
            var stop = request.Stop[i];
            var chunkLength = array.Chunks[i];
            if (start < 0 || stop <= start || stop > array.Shape[i] || chunkLength <= 0)
            {
                throw new ArgumentException("Zarr subset bounds and chunk dimensions must be valid.", nameof(request));
            }

            subsetElements = checked(subsetElements * (stop - start));
            var firstChunk = start / chunkLength;
            var stopChunk = ((stop - 1) / chunkLength) + 1;
            chunkCount = checked(chunkCount * (stopChunk - firstChunk));
            chunkBytes = checked(chunkBytes * chunkLength);
        }

        var decodedSubsetBytes = checked(subsetElements * elementSize);
        var outputCells = checked((long)outputWidth * outputHeight);
        var rgbaOutputBytes = checked(outputCells * 4L);

        return new RasterCapacityWork(
            WebOutputCells: outputCells,
            WebOutputBytes: checked(decodedSubsetBytes + rgbaOutputBytes),
            ObjectRangeRequests: chunkCount,
            ObjectRangeBytes: checked(chunkCount * chunkBytes),
            PostGisWorkUnits: 0);
    }
}
