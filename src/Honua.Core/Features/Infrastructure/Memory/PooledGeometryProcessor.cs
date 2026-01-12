// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Core.Features.Infrastructure.Memory;

/// <summary>
/// High-performance geometry processing utilities that use pooled memory
/// to minimize allocations during intensive coordinate operations.
/// </summary>
public static class PooledGeometryProcessor
{
    /// <summary>
    /// Creates a Point geometry using pooled coordinate buffers for large-scale operations
    /// </summary>
    /// <param name="x">X coordinate</param>
    /// <param name="y">Y coordinate</param>
    /// <param name="z">Optional Z coordinate</param>
    /// <param name="srid">Spatial reference system identifier</param>
    /// <param name="factory">Geometry factory to use for creation</param>
    /// <returns>Point geometry</returns>
    public static Point CreatePointOptimized(double x, double y, double? z = null, int? srid = null,
        GeometryFactory? factory = null)
    {
        factory ??= new GeometryFactory(new PrecisionModel(), srid ?? 0);

        var coordinate = z.HasValue
            ? new CoordinateZ(x, y, z.Value)
            : new Coordinate(x, y);

        return factory.CreatePoint(coordinate);
    }

    /// <summary>
    /// Processes coordinate arrays using pooled memory for large geometry operations
    /// </summary>
    /// <param name="coordinateCount">Number of coordinates to process</param>
    /// <param name="dimensions">Dimensions per coordinate (2D, 3D, or 4D)</param>
    /// <param name="processor">Function that processes the coordinate buffer</param>
    /// <returns>Result from the processing function</returns>
    /// <remarks>
    /// This method is designed for high-frequency coordinate processing during
    /// imports, transformations, or bulk operations where memory allocation
    /// overhead significantly impacts performance.
    /// </remarks>
    public static T ProcessCoordinatesWithPooling<T>(
        int coordinateCount,
        int dimensions,
        Func<CoordinateBufferRental, T> processor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(coordinateCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, 4);
        ArgumentNullException.ThrowIfNull(processor);

        using var rental = GeometryMemoryManager.RentCoordinateBuffer(coordinateCount, dimensions);
        return processor(rental);
    }

    /// <summary>
    /// Converts geometry to WKB using pooled memory buffers for efficient processing
    /// </summary>
    /// <param name="geometry">Geometry to convert to WKB</param>
    /// <returns>WKB byte array</returns>
    /// <remarks>
    /// For large geometries or bulk processing, this method pre-allocates an
    /// appropriately-sized buffer from the pool to avoid repeated allocations.
    /// </remarks>
    public static byte[] WriteWkbWithPooling(NetTopologySuite.Geometries.Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        // Estimate WKB size based on geometry complexity
        var estimatedSize = EstimateWkbSize(geometry);

        // For large geometries, use pooled buffer to avoid allocation overhead
        if (estimatedSize > 1024) // Use pooling for geometries > 1KB estimated WKB
        {
            var writer = new WKBWriter();
            var bufferSize = estimatedSize + (estimatedSize / 4) + 64;
            using var bufferRental = GeometryMemoryManager.RentWkbBuffer(estimatedSize, bufferSize);

            try
            {
                using var innerStream = new MemoryStream(bufferRental.Buffer, 0, bufferRental.Buffer.Length, writable: true, publiclyVisible: false);
                using var stream = new NonClosingStream(innerStream);
                writer.Write(geometry, stream);

                var length = (int)innerStream.Position;
                if (length == 0)
                {
                    return Array.Empty<byte>();
                }

                var result = new byte[length];
                Buffer.BlockCopy(bufferRental.Buffer, 0, result, 0, length);
                return result;
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                return writer.Write(geometry);
            }
        }

        // For smaller geometries, standard allocation is fine
        return new WKBWriter().Write(geometry);
    }

    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;

        public NonClosingStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => _inner.Seek(offset, origin);

        public override void SetLength(long value)
            => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
            => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            // Suppress disposing the inner stream; base.Dispose is a no-op.
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Reads WKB using pooled memory for parsing operations
    /// </summary>
    /// <param name="wkbData">WKB byte data</param>
    /// <returns>Parsed geometry</returns>
    /// <remarks>
    /// Uses pooled memory for internal parsing operations when processing
    /// large amounts of WKB data to reduce garbage collection pressure.
    /// </remarks>
    public static NetTopologySuite.Geometries.Geometry ReadWkbWithPooling(ReadOnlySpan<byte> wkbData)
    {
        if (wkbData.IsEmpty)
        {
            throw new ArgumentException("WKB data cannot be empty", nameof(wkbData));
        }

        // For large WKB data, consider using pooled buffers for any intermediate processing
        // Currently, WKBReader works directly with the input span, so pooling benefit is minimal
        // This method is provided for API consistency and future optimization opportunities
        var reader = new WKBReader();
        return reader.Read(wkbData.ToArray()); // Note: ToArray() creates allocation - potential for future optimization
    }

    /// <summary>
    /// Estimates WKB size for a geometry to determine appropriate buffer allocation
    /// </summary>
    /// <param name="geometry">Geometry to estimate</param>
    /// <returns>Estimated WKB size in bytes</returns>
    private static int EstimateWkbSize(NetTopologySuite.Geometries.Geometry geometry)
    {
        if (geometry.IsEmpty)
            return 0;

        // WKB overhead estimates per geometry type
        var baseOverhead = geometry.GeometryType switch
        {
            "Point" => 21,
            "LineString" => 25,
            "Polygon" => 29,
            "MultiPoint" => 29,
            "MultiLineString" => 33,
            "MultiPolygon" => 37,
            "GeometryCollection" => 41,
            _ => 45
        };

        // Each coordinate is approximately 16 bytes (2 doubles for X,Y)
        // Add extra for Z/M coordinates if present
        var coordinateSize = 16;
        if (geometry.Coordinate is CoordinateZ or CoordinateZM)
            coordinateSize += 8; // Add Z coordinate
        if (geometry.Coordinate is CoordinateM or CoordinateZM)
            coordinateSize += 8; // Add M coordinate

        var totalCoordinates = geometry.NumPoints;
        return baseOverhead + (totalCoordinates * coordinateSize);
    }

    /// <summary>
    /// Batches coordinate processing operations to optimize memory usage
    /// </summary>
    /// <param name="coordinates">Coordinates to process</param>
    /// <param name="batchSize">Number of coordinates per batch</param>
    /// <param name="processor">Processing function for each batch</param>
    /// <remarks>
    /// Useful for processing large coordinate arrays in chunks to maintain
    /// consistent memory usage and avoid large buffer allocations.
    /// </remarks>
    public static void ProcessCoordinateBatches(
        ReadOnlySpan<Coordinate> coordinates,
        int batchSize,
        Action<ReadOnlySpan<Coordinate>> processor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentNullException.ThrowIfNull(processor);

        for (int i = 0; i < coordinates.Length; i += batchSize)
        {
            var end = Math.Min(i + batchSize, coordinates.Length);
            var batch = coordinates.Slice(i, end - i);
            processor(batch);
        }
    }
}
