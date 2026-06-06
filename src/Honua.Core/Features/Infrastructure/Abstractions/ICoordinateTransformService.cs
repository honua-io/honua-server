// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Crs;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Transforms coordinates between spatial reference systems with automatic fallback
/// from fast in-memory paths to authoritative database-backed transforms.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Supported transform paths:</strong>
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Path</term>
///     <description>Method / Latency</description>
///   </listheader>
///   <item>
///     <term>Same SRID (identity)</term>
///     <description>In-memory — near-zero latency</description>
///   </item>
///   <item>
///     <term>Web Mercator aliases (3857 / 900913 / 102100 / 102113 / 3785)</term>
///     <description>In-memory — near-zero latency</description>
///   </item>
///   <item>
///     <term>WGS 84 (4326) ↔ Web Mercator (3857)</term>
///     <description>In-memory — near-zero latency</description>
///   </item>
///   <item>
///     <term>All other SRID pairs (e.g. NAD 83 / 4269, NAD 27 / 4267, UTM zones)</term>
///     <description>PostGIS <c>ST_Transform</c> — database round-trip</description>
///   </item>
/// </list>
/// <para>
/// <strong>Unsupported:</strong> SRID pairs where either SRID is absent from the
/// PostGIS <c>spatial_ref_sys</c> table. These return <see langword="null"/>.
/// </para>
/// <para>
/// NAD 83 (4269) → WGS 84 (4326) is <em>not</em> treated as an identity transform;
/// the offset can reach ~2 m. All non-trivial datum shifts use the authoritative
/// PROJ pipeline selected by PostGIS.
/// </para>
/// </remarks>
public interface ICoordinateTransformService
{
    /// <summary>
    /// Transforms a bounding-box extent from one SRID to another.
    /// </summary>
    /// <param name="minX">Minimum X (longitude or easting).</param>
    /// <param name="minY">Minimum Y (latitude or northing).</param>
    /// <param name="maxX">Maximum X (longitude or easting).</param>
    /// <param name="maxY">Maximum Y (latitude or northing).</param>
    /// <param name="fromSrid">Source EPSG SRID.</param>
    /// <param name="toSrid">Target EPSG SRID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Transformed extent as <c>(MinX, MinY, MaxX, MaxY)</c>, or <see langword="null"/>
    /// when the transform cannot be performed (unknown SRID, PostGIS error).
    /// </returns>
    ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX, double minY, double maxX, double maxY,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transforms a single point from one SRID to another.
    /// </summary>
    /// <param name="x">X coordinate (longitude or easting).</param>
    /// <param name="y">Y coordinate (latitude or northing).</param>
    /// <param name="fromSrid">Source EPSG SRID.</param>
    /// <param name="toSrid">Target EPSG SRID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Transformed point as <c>(X, Y)</c>, or <see langword="null"/>
    /// when the transform cannot be performed (unknown SRID, PostGIS error).
    /// </returns>
    ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transforms a single point using an explicitly selected datum transformation.
    /// </summary>
    /// <param name="x">X coordinate (longitude or easting).</param>
    /// <param name="y">Y coordinate (latitude or northing).</param>
    /// <param name="fromSrid">Source EPSG SRID.</param>
    /// <param name="toSrid">Target EPSG SRID.</param>
    /// <param name="selection">
    /// The resolved datum transformation. When it carries a PROJ pipeline, the
    /// authoritative 3-argument <c>ST_Transform</c> is used so the result matches the
    /// selected (Esri-parity) pipeline. When <see langword="null"/>, behaves like the
    /// SRID-only overload.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Transformed point as <c>(X, Y)</c>, or <see langword="null"/> when the transform
    /// cannot be performed (unknown SRID, missing grid, PostGIS error).
    /// </returns>
    /// <remarks>
    /// Provided as a default interface method so existing implementations remain
    /// source-compatible; the default ignores the pipeline and delegates to the
    /// SRID-only overload. The PostGIS implementation overrides this to honor the
    /// selected pipeline via 3-argument <c>ST_Transform</c>.
    /// </remarks>
    ValueTask<(double X, double Y)?> TransformPointAsync(
        double x, double y,
        int fromSrid, int toSrid,
        DatumTransformationSelection? selection,
        CancellationToken cancellationToken = default)
        => TransformPointAsync(x, y, fromSrid, toSrid, cancellationToken);
}
