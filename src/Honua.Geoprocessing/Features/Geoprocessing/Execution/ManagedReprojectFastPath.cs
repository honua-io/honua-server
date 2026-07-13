// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;

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

        if (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return true;
        }

        return SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && toSrid == 4326;
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
            || (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid));

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
