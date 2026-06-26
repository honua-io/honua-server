// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Single source of truth for the SRID pairs the lean, GDAL-free
/// <c>transform.reproject</c> path (<see cref="ReprojectTransformExecutor"/>) can
/// serve in-memory via the shared <c>CoordinateTransformer</c>: identity, the Web
/// Mercator aliases, and WGS 84 (4326) ↔ Web Mercator. Every OTHER pair is a
/// datum/grid shift that requires PROJ's transformation pipelines and is therefore
/// routed to the heavyweight native worker (<c>GdalVectorReprojectJobExecutor</c>
/// via <c>ogr2ogr -s_srs/-t_srs</c>).
///
/// The geoprocessing submit path (<c>GeoprocessingJobService.ResolveRequiredRuntimeProfile</c>)
/// consults <see cref="RequiresNativeWorker(int, int)"/> to ESCALATE such a job to
/// the native runtime profile BEFORE it is queued, so the claim fence hands the job
/// to the GDAL worker rather than the lean dispatcher rejecting it at execution time.
/// Centralizing the predicate here keeps the managed executor's accept-set and the
/// submit-path escalation provably in lock-step: a pair is native iff it is NOT a
/// managed fast-path, with no second copy of the rule to drift.
/// </summary>
internal static class ManagedReprojectFastPath
{
    /// <summary>
    /// The Web Mercator SRID and its common authority aliases. Reprojecting between
    /// any two of these is a no-op datum-wise (same WGS 84 datum, same projection
    /// math) and is served on the managed path.
    /// </summary>
    private static readonly HashSet<int> WebMercatorAliases =
        new() { 3857, 900913, 102100, 102113, 3785 };

    /// <summary>
    /// Returns <c>true</c> when reprojecting from <paramref name="fromSrid"/> to
    /// <paramref name="toSrid"/> is one of the managed in-memory fast paths: identity
    /// (same SRID), Web-Mercator-alias ↔ Web-Mercator-alias, or WGS 84 (4326) ↔ Web
    /// Mercator. These need no datum shift, so the lean executor handles them.
    /// </summary>
    public static bool IsManagedFastPath(int fromSrid, int toSrid)
    {
        if (fromSrid == toSrid)
        {
            return true;
        }

        if (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        return WebMercatorAliases.Contains(fromSrid) && toSrid == 4326;
    }

    /// <summary>
    /// Returns <c>true</c> when the SRID pair is NOT a managed fast path and therefore
    /// requires the native PROJ-backed worker (a datum/grid shift). The inverse of
    /// <see cref="IsManagedFastPath(int, int)"/>.
    /// </summary>
    public static bool RequiresNativeWorker(int fromSrid, int toSrid)
        => !IsManagedFastPath(fromSrid, toSrid);

    /// <summary>
    /// Returns <c>true</c> when the managed transform can copy geometries through
    /// unchanged: identity or any Web-Mercator-alias ↔ Web-Mercator-alias pair (same
    /// projected coordinate space, so no per-coordinate transform is required). A
    /// 4326 ↔ Web Mercator pair is a managed fast path but NOT a passthrough — it
    /// still runs the in-memory coordinate transform.
    /// </summary>
    public static bool IsPassthrough(int fromSrid, int toSrid)
        => fromSrid == toSrid
            || (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid));

    /// <summary>
    /// Parses a positive-integer SRID step input. Returns <c>false</c> (with
    /// <paramref name="srid"/> = 0) when the value is missing, non-numeric, or not a
    /// positive integer — matching the managed executor's <c>ReadSrid</c> contract so
    /// the submit path and the executor agree on what a valid SRID input is.
    /// </summary>
    public static bool TryParseSrid(string? raw, out int srid)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid) || srid <= 0)
        {
            srid = 0;
            return false;
        }

        return true;
    }
}
