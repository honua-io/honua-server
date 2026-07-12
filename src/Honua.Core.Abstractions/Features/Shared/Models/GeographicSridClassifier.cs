// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

#pragma warning disable CA1716 // Preserve the stable Shared namespace used across existing contracts.
namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Canonical, registry-agnostic classification of EPSG SRIDs as geographic (latitude/longitude
/// degree) coordinate systems. This is the single source of truth for the static SRID allowlists
/// that previously lived — and silently diverged — in <c>BoundingBox.IsGeographicSrid</c>,
/// <c>SpatialConstants.GeographicSrids</c>, <c>DistanceConversions.IsGeographicSrid</c>,
/// the ImageServer mensuration helpers, <c>TileMatrixSetRegistry</c>, and the Postgres provider
/// fallbacks (#2732).
/// </summary>
/// <remarks>
/// <para>
/// Authoritative geographic-ness is derived from the CRS registry (<c>spatial_ref_sys</c> WKT /
/// proj4, exposed through <c>ICrsRegistry</c> and <c>CrsDefinition.IsGeographic</c>); the lists
/// here are the <b>bootstrap / fallback</b> used only where a registry lookup is unavailable
/// (static model methods, provider paths that miss a <c>spatial_ref_sys</c> row, offline math).
/// The Postgres <c>CrsMetrics</c> path keeps its primary <c>spatial_ref_sys</c> derivation and
/// only consults this fallback when the SRID is absent from the registry.
/// </para>
/// <para>
/// Two deliberately different buckets are exposed because "is this lat/lon?" and "is this safe to
/// run WGS 84 geodesic distance on?" are different questions:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="GeographicSrids"/> / <see cref="IsGeographicSrid(int)"/> — the broad union of
///       every code any prior list treated as geographic. Drives axis-order decisions (WMS 1.3.0
///       BBOX, WFS 2.0 CRS, FES filter geometries via <c>SpatialReference.IsGeographic</c>),
///       degree-based scale math, spherical area, and general degree/planar routing. Every entry
///       is a confirmed geographic 2D/3D CRS; the geocentric (e.g. EPSG:4978) and projected codes
///       that share the EPSG 4xxx block are intentionally excluded, so no broad numeric-range
///       heuristic is used.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="GeodesicDistanceSafeSrids"/> / <see cref="IsGeodesicDistanceSafeSrid(int)"/> —
///       the narrow allowlist of degree CRSes for which a WGS 84 spheroid geodesic distance
///       (PostGIS <c>ST_Distance_Spheroid</c> / DuckDB <c>ST_Distance_Spheroid</c>, KNN geodesic
///       gate) is applied directly. It is intentionally <b>narrower</b> than
///       <see cref="GeographicSrids"/>: the spheroid functions assume WGS 84, and running them on
///       an unlisted geographic CRS with an unestablished datum (or comparing metres to degrees
///       via planar <c>ST_DWithin</c>) would silently return whole-planet matches, so the DuckDB
///       provider rejects distance filters on unlisted geographic codes rather than guess (#2731).
///       Do not widen this list to <see cref="GeographicSrids"/> without re-validating the
///       provider distance paths and their tests.
///     </description>
///   </item>
/// </list>
/// </remarks>
public static class GeographicSridClassifier
{
    /// <summary>
    /// Broad union of every EPSG SRID any Honua classifier historically treated as geographic
    /// (lat/lon degrees). Used for axis order, degree scale math, spherical area, and general
    /// degree/planar routing. See the type remarks for why this differs from
    /// <see cref="GeodesicDistanceSafeSrids"/>.
    /// </summary>
    public static readonly ImmutableArray<int> GeographicSrids =
    [
        4149,   // CH1903
        4167,   // NZGD2000
        4171,   // RGF93
        4230,   // ED50
        4258,   // ETRS89
        4267,   // NAD27
        4269,   // NAD83
        4275,   // NTF
        4277,   // OSGB 1936
        4283,   // GDA94
        4326,   // WGS 84
        4490,   // CGCS2000
        4612,   // JGD2000
        4617,   // NAD83(CSRS)
        4618,   // SAD69
        4619,   // SWEREF99
        4674,   // SIRGAS 2000
        4759,   // NAD83(NSRS2007)
        4979,   // WGS 84 (3D)
        6668,   // JGD2011
        7844,   // GDA2020
    ];

    /// <summary>
    /// Narrow allowlist of geographic (degree) SRIDs for which a WGS 84 spheroid geodesic distance
    /// is applied directly by the provider distance paths. Intentionally a subset of
    /// <see cref="GeographicSrids"/>; see the type remarks and #2731 before changing it.
    /// </summary>
    public static readonly ImmutableArray<int> GeodesicDistanceSafeSrids =
    [
        4258,   // ETRS89
        4267,   // NAD27
        4269,   // NAD83
        4283,   // GDA94
        4326,   // WGS 84
        4617,   // NAD83(CSRS)
        4759,   // NAD83(NSRS2007)
    ];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="srid"/> is a known geographic
    /// (latitude/longitude) CRS. A <see langword="null"/> SRID is treated as geographic, matching
    /// the historical <c>BoundingBox</c> convention (the default assumed CRS is WGS 84).
    /// </summary>
    /// <param name="srid">EPSG SRID, or <see langword="null"/> for the default CRS.</param>
    /// <returns><see langword="true"/> when the SRID is geographic; otherwise <see langword="false"/>.</returns>
    public static bool IsGeographicSrid(int? srid)
        => srid is null || GeographicSrids.Contains(srid.Value);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="srid"/> is a known geographic
    /// (latitude/longitude) CRS.
    /// </summary>
    /// <param name="srid">EPSG SRID.</param>
    /// <returns><see langword="true"/> when the SRID is geographic; otherwise <see langword="false"/>.</returns>
    public static bool IsGeographicSrid(int srid)
        => GeographicSrids.Contains(srid);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="srid"/> is a geographic (degree) CRS on
    /// the narrow allowlist that is safe to run a WGS 84 spheroid geodesic distance on. See
    /// <see cref="GeodesicDistanceSafeSrids"/>.
    /// </summary>
    /// <param name="srid">EPSG SRID.</param>
    /// <returns>
    /// <see langword="true"/> when the SRID is on the geodesic-safe allowlist; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool IsGeodesicDistanceSafeSrid(int srid)
        => GeodesicDistanceSafeSrids.Contains(srid);
}
