// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

#pragma warning disable CA1716 // Preserve the stable Shared namespace used across existing contracts.
namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Registry-backed classification of EPSG SRIDs as geographic (latitude/longitude degree)
/// coordinate systems. This is the DI-reachable seam requested by #2794: it derives
/// geographic-ness from the live spatial reference registry (<c>ICrsRegistry</c> /
/// <c>CrsDefinition.IsGeographic</c>, which classifies from <c>spatial_ref_sys</c> WKT/proj4)
/// and only falls back to the static bootstrap allowlist in <see cref="GeographicSridClassifier"/>
/// when the registry has no answer (SRID absent, or no provider registered a registry).
/// </summary>
/// <remarks>
/// <para>
/// Prefer this service over the static <see cref="GeographicSridClassifier"/> at any call site
/// that can reach dependency injection. The static class remains the correct answer for
/// static-only, hot-path, or singleton contexts that cannot inject an async dependency
/// (<c>BoundingBox</c>, <c>SpatialReference</c>, <c>TileMatrixSetRegistry</c> custom-gridset
/// seeding), where it is documented as the fallback tier. The registry-backed answer is strictly
/// more accurate: genuinely geographic codes outside the static 21-code list, and geocentric
/// codes that share the EPSG 4000–4999 block, are classified correctly from their WKT rather than
/// from a numeric-range heuristic.
/// </para>
/// <para>
/// The interface deliberately lives in <c>Honua.Core.Abstractions</c> and references no
/// infrastructure type; the registry-composing implementation lives in <c>Honua.Core</c> where
/// <c>ICrsRegistry</c> is defined, preserving the Core-abstractions dependency direction.
/// </para>
/// </remarks>
public interface IGeographicSridClassifier
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="srid"/> is a geographic
    /// (latitude/longitude degree) CRS, consulting the registry first and the static
    /// <see cref="GeographicSridClassifier.IsGeographicSrid(int)"/> allowlist as a fallback. Use
    /// for axis-order and degree-vs-planar routing (the registry-backed counterpart of
    /// <see cref="GeographicSridClassifier.IsGeographicSrid(int)"/>).
    /// </summary>
    /// <param name="srid">EPSG SRID.</param>
    /// <param name="cancellationToken">Cancellation token for async registry resolution.</param>
    /// <returns><see langword="true"/> when the SRID is geographic; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> IsGeographicAsync(int srid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="srid"/> should be measured as geographic
    /// (lon/lat degrees) in the offline mensuration path. Consults the registry first; when the
    /// registry has no answer it falls back to
    /// <see cref="GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(int)"/> (the
    /// broad list plus the EPSG 4000–4999 geographic-block range heuristic). Because the registry
    /// classifies geocentric codes as projected from their WKT, this is the authoritative answer for
    /// the ImageServer offline area/centroid/geodesic-routing consumers and supersedes the
    /// conservative geocentric-exclusion subset used by the static heuristic (#2794).
    /// </summary>
    /// <param name="srid">EPSG SRID.</param>
    /// <param name="cancellationToken">Cancellation token for async registry resolution.</param>
    /// <returns>
    /// <see langword="true"/> when the SRID measures as geographic degrees; otherwise
    /// <see langword="false"/>.
    /// </returns>
    ValueTask<bool> IsGeographicForMeasurementAsync(int srid, CancellationToken cancellationToken = default);
}
