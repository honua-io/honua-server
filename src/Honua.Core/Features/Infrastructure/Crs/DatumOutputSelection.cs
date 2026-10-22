// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Infrastructure.Crs;

/// <summary>
/// Resolves the catalog pipeline for a draw or sample reprojection. Feature query
/// output already carries <see cref="DatumTransformationSelection"/>; tiles, scene
/// publishing, and raster identify call this when the caller did not.
/// </summary>
internal static class DatumOutputSelection
{
    private const int Wgs84 = 4326;
    private const int Nad83 = 4269;

    /// <summary>
    /// Catalog selection for reprojecting <paramref name="fromSrid"/> to <paramref name="toSrid"/>.
    /// </summary>
    /// <remarks>
    /// A direct catalog pair wins. Web Mercator uses the WGS 84 geographic pair and
    /// <c>DatumTransformSql</c> composes the projection. NAD27 and the other grids that
    /// reach WGS 84 only through NAD83 use that grid; the NAD83 to WGS 84 step stays the
    /// catalog no-operation. No catalog entry returns null so the caller keeps two-argument
    /// <c>ST_Transform</c>.
    /// </remarks>
    public static DatumTransformationSelection? Resolve(
        IDatumTransformationCatalog catalog,
        int fromSrid,
        int toSrid)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (fromSrid <= 0 || toSrid <= 0 || fromSrid == toSrid)
        {
            return null;
        }

        if (catalog.TryGetDefault(fromSrid, toSrid, out var direct))
        {
            return direct;
        }

        var geographicTarget = SpatialReferenceExtensions.IsWebMercatorSrid(toSrid) ? Wgs84 : toSrid;
        if (fromSrid == geographicTarget)
        {
            return null;
        }

        if (catalog.TryGetDefault(fromSrid, geographicTarget, out var geographic))
        {
            return geographic;
        }

        // A Web Mercator click sampled on a geographic raster uses the WGS 84 pair.
        if (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid)
            && catalog.TryGetDefault(Wgs84, toSrid, out var fromWgs84))
        {
            return fromWgs84;
        }

        // NAD27 (and the other NADCON/NTv2 datums) have no direct WGS 84 row. Their
        // catalog default is a grid to NAD83, and NAD83 to WGS 84 is the no-operation.
        // Label the grid output as WGS 84 so a Web Mercator tile composes only the
        // projection afterwards. A non-noop NAD83 step is a realization pipeline (#5186)
        // and is left unset. The same grid, inverted, samples a NAD27 raster from WGS 84.
        if (geographicTarget == Wgs84
            && fromSrid != Nad83
            && TryNad83Grid(catalog, fromSrid, out var forward))
        {
            return forward;
        }

        if (GeographicBase(fromSrid) == Wgs84
            && toSrid != Nad83
            && TryNad83Grid(catalog, toSrid, out var rasterForward))
        {
            return Inverse(rasterForward);
        }

        return null;
    }

    private static int GeographicBase(int srid)
        => SpatialReferenceExtensions.IsWebMercatorSrid(srid) ? Wgs84 : srid;

    private static bool TryNad83Grid(
        IDatumTransformationCatalog catalog,
        int datumSrid,
        out DatumTransformationSelection forward)
    {
        forward = null!;
        // WGS 84 to NAD83 is the inverse no-operation. It is not a grid, and treating
        // it as one would relabel a Web Mercator to WGS 84 projection as a datum step.
        if (datumSrid == Nad83
            || datumSrid == Wgs84
            || !catalog.TryGetDefault(datumSrid, Nad83, out var toNad83)
            || toNad83.ProjPipeline is not { Length: > 0 } pipeline
            || pipeline.Trim().Equals("+proj=noop", StringComparison.Ordinal)
            || !catalog.TryGetDefault(Nad83, Wgs84, out var nad83ToWgs84)
            || nad83ToWgs84.ProjPipeline?.Trim() != "+proj=noop")
        {
            return false;
        }

        forward = toNad83 with { ToSrid = Wgs84 };
        return true;
    }

    /// <summary>
    /// Swaps the selection so a tile envelope in the output CRS can be filtered in the layer CRS.
    /// </summary>
    public static DatumTransformationSelection Inverse(DatumTransformationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection with
        {
            FromSrid = selection.ToSrid,
            ToSrid = selection.FromSrid,
            TransformForward = !selection.TransformForward
        };
    }
}
