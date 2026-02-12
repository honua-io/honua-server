// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.GeometryService.Abstractions;

/// <summary>
/// Provides server-side geometry operations (buffer, simplify, project, union)
/// backed by a spatial database engine such as PostGIS.
/// </summary>
public interface IGeometryOperationService
{
    /// <summary>
    /// Buffers a geometry by the specified distance.
    /// </summary>
    /// <param name="wkb">Input geometry in WKB format.</param>
    /// <param name="srid">Spatial reference ID of the input geometry.</param>
    /// <param name="distance">Buffer distance in the units of the spatial reference (or meters when geodesic).</param>
    /// <param name="geodesic">When true, performs a geodesic (geography-based) buffer.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Buffered geometry in WKB format.</returns>
    Task<byte[]> BufferAsync(byte[] wkb, int srid, double distance, bool geodesic, CancellationToken ct = default);

    /// <summary>
    /// Simplifies a geometry using the Douglas-Peucker algorithm.
    /// </summary>
    /// <param name="wkb">Input geometry in WKB format.</param>
    /// <param name="tolerance">Simplification tolerance in the units of the geometry's spatial reference.</param>
    /// <param name="preserveTopology">When true, uses topology-preserving simplification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Simplified geometry in WKB format.</returns>
    Task<byte[]> SimplifyAsync(byte[] wkb, double tolerance, bool preserveTopology, CancellationToken ct = default);

    /// <summary>
    /// Reprojects a geometry from one spatial reference to another.
    /// </summary>
    /// <param name="wkb">Input geometry in WKB format.</param>
    /// <param name="fromSrid">Source spatial reference ID.</param>
    /// <param name="toSrid">Target spatial reference ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reprojected geometry in WKB format.</returns>
    Task<byte[]> ProjectAsync(byte[] wkb, int fromSrid, int toSrid, CancellationToken ct = default);

    /// <summary>
    /// Makes a geometry topologically valid (fixes self-intersections, ring orientation, etc.).
    /// This corresponds to the ArcGIS REST API "simplify" operation, which performs topological
    /// correction rather than generalization.
    /// </summary>
    /// <param name="wkb">Input geometry in WKB format.</param>
    /// <param name="srid">Spatial reference ID of the input geometry.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Topologically valid geometry in WKB format.</returns>
    Task<byte[]> MakeValidAsync(byte[] wkb, int srid, CancellationToken ct = default);

    /// <summary>
    /// Computes the union of multiple geometries.
    /// </summary>
    /// <param name="wkbs">Array of input geometries in WKB format.</param>
    /// <param name="srid">Spatial reference ID of the input geometries.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Unioned geometry in WKB format.</returns>
    Task<byte[]> UnionAsync(byte[][] wkbs, int srid, CancellationToken ct = default);
}
