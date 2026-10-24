// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    /// When <paramref name="area"/> is set, a pair that has areas of use must match exactly
    /// one of them. No match, or more than one, stays null instead of the no-envelope default
    /// (the CONUS NAD27 grid), which does not cover an Alaska tile or the world at zoom 0.
    /// </remarks>
    public static DatumTransformationSelection? Resolve(
        IDatumTransformationCatalog catalog,
        int fromSrid,
        int toSrid,
        DatumAreaEnvelope? area = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (fromSrid <= 0 || toSrid <= 0 || fromSrid == toSrid)
        {
            return null;
        }

        if (TryPair(catalog, fromSrid, toSrid, area, out var direct))
        {
            return direct;
        }

        if (area is not null && catalog.HasAreaOfUse(fromSrid, toSrid))
        {
            return null;
        }

        var geographicTarget = SpatialReferenceExtensions.IsWebMercatorSrid(toSrid) ? Wgs84 : toSrid;
        if (fromSrid == geographicTarget)
        {
            return null;
        }

        if (TryPair(catalog, fromSrid, geographicTarget, area, out var geographic))
        {
            return geographic;
        }

        if (area is not null && catalog.HasAreaOfUse(fromSrid, geographicTarget))
        {
            return null;
        }

        // A Web Mercator click sampled on a geographic raster uses the WGS 84 pair.
        if (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid)
            && TryPair(catalog, Wgs84, toSrid, area, out var fromWgs84))
        {
            return fromWgs84;
        }

        if (area is not null
            && SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid)
            && catalog.HasAreaOfUse(Wgs84, toSrid))
        {
            return null;
        }

        // NAD27 (and the other NADCON/NTv2 datums) have no direct WGS 84 row. Their
        // catalog default is a grid to NAD83, and NAD83 to WGS 84 is the no-operation.
        // Label the grid output as WGS 84 so a Web Mercator tile composes only the
        // projection afterwards. A non-noop NAD83 step is a realization pipeline (#5186)
        // and is left unset. The same grid, inverted, samples a NAD27 raster from WGS 84.
        if (geographicTarget == Wgs84
            && fromSrid != Nad83
            && TryNad83Grid(catalog, fromSrid, area, out var forward))
        {
            return forward;
        }

        if (GeographicBase(fromSrid) == Wgs84
            && toSrid != Nad83
            && TryNad83Grid(catalog, toSrid, area, out var rasterForward))
        {
            return Inverse(rasterForward);
        }

        return null;
    }

    /// <summary>
    /// Geographic degrees for a tile envelope. Web Mercator bounds are inverse-projected.
    /// Any other projected tile CRS returns null; the caller then keeps two-argument
    /// <c>ST_Transform</c> when the datum pair has areas of use.
    /// </summary>
    public static DatumAreaEnvelope? AreaForTile(
        double xMin,
        double yMin,
        double xMax,
        double yMax,
        int tileSrid)
    {
        if (GeographicSridClassifier.IsGeographicSrid(tileSrid))
        {
            return new DatumAreaEnvelope(xMin, yMin, xMax, yMax);
        }

        if (!SpatialReferenceExtensions.IsWebMercatorSrid(tileSrid))
        {
            return null;
        }

        var (west, south) = WebMercatorMath.WebMercatorToLonLat(xMin, yMin);
        var (east, north) = WebMercatorMath.WebMercatorToLonLat(xMax, yMax);
        return new DatumAreaEnvelope(west, south, east, north);
    }

    private static bool TryPair(
        IDatumTransformationCatalog catalog,
        int fromSrid,
        int toSrid,
        DatumAreaEnvelope? area,
        out DatumTransformationSelection? selection)
    {
        if (area is { } envelope)
        {
            if (catalog.TryGetForEnvelope(
                    fromSrid,
                    toSrid,
                    envelope.West,
                    envelope.South,
                    envelope.East,
                    envelope.North,
                    out selection))
            {
                return true;
            }

            if (catalog.HasAreaOfUse(fromSrid, toSrid))
            {
                selection = null;
                return false;
            }
        }

        return catalog.TryGetDefault(fromSrid, toSrid, out selection);
    }

    private static int GeographicBase(int srid)
        => SpatialReferenceExtensions.IsWebMercatorSrid(srid) ? Wgs84 : srid;

    private static bool TryNad83Grid(
        IDatumTransformationCatalog catalog,
        int datumSrid,
        DatumAreaEnvelope? area,
        out DatumTransformationSelection forward)
    {
        forward = null!;
        // WGS 84 to NAD83 is the inverse no-operation. It is not a grid, and treating
        // it as one would relabel a Web Mercator to WGS 84 projection as a datum step.
        if (datumSrid == Nad83
            || datumSrid == Wgs84
            || !catalog.TryGetDefault(Nad83, Wgs84, out var nad83ToWgs84)
            || nad83ToWgs84.ProjPipeline?.Trim() != "+proj=noop")
        {
            return false;
        }

        // A tile whose bounds are known must use the one grid that contains them.
        // Several areas, or none, keep two-argument ST_Transform instead of CONUS.
        DatumTransformationSelection? toNad83;
        if (area is not null && catalog.HasAreaOfUse(datumSrid, Nad83))
        {
            if (!TryPair(catalog, datumSrid, Nad83, area, out toNad83))
            {
                return false;
            }
        }
        else if (!catalog.TryGetDefault(datumSrid, Nad83, out toNad83))
        {
            return false;
        }

        if (toNad83?.ProjPipeline is not { Length: > 0 } pipeline
            || pipeline.Trim().Equals("+proj=noop", StringComparison.Ordinal))
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
