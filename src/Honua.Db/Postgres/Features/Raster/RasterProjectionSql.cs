// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;

namespace Honua.Db.Postgres.Features.Raster;

internal static class RasterProjectionSql
{
    internal const string DefaultResamplingAlgorithm = "NearestNeighbor";

    private static readonly FrozenSet<string> AllowedResamplingAlgorithms =
        new[] { "NearestNeighbor", "Bilinear", "Cubic", "CubicSpline", "Lanczos" }
            .ToFrozenSet(StringComparer.Ordinal);

    internal static string ValidateResamplingAlgorithm(string? algorithm)
    {
        var resolved = string.IsNullOrEmpty(algorithm) ? DefaultResamplingAlgorithm : algorithm;
        if (!AllowedResamplingAlgorithms.Contains(resolved))
        {
            throw new ArgumentException($"Unsupported resampling algorithm: {resolved}", nameof(algorithm));
        }

        return resolved;
    }

    // Unlike the geometry overload, ST_Transform(raster, its_own_srid) is not an
    // identity: GDAL can square non-square pixels and change the grid and extent.
    // OFFSET 0 prevents PostgreSQL from pulling up the binding and evaluating an
    // expensive raster expression again for the CASE predicate and selected arm.
    internal static string TransformIfNeeded(
        string raster,
        string targetSrid,
        string algorithm = DefaultResamplingAlgorithm)
    {
        var validated = ValidateResamplingAlgorithm(algorithm);
        var transformCall = string.Equals(validated, DefaultResamplingAlgorithm, StringComparison.Ordinal)
            ? $"ST_Transform(projection_source.rast, {targetSrid})"
            : $"ST_Transform(projection_source.rast, {targetSrid}, '{validated}')";

        return $"""
            (SELECT CASE WHEN ST_SRID(projection_source.rast) = {targetSrid}
                THEN projection_source.rast
                ELSE {transformCall} END
             FROM (SELECT {raster} AS rast OFFSET 0) projection_source)
            """;
    }

    // GDAL can change the geographic footprint of non-square or rotated rasters
    // while resizing. Resize the pixel array on a unit grid, then restore the
    // source origin and proportionally scaled geographic basis vectors.
    // Keep the same optimizer barrier here: resizing reads its input repeatedly.
    // Set the restored basis numerically: PostGIS's numeric ST_SetGeoReference overload
    // formats doubles as text, then rejects scientific notation for small pixel scales.
    internal static string ResizePreservingGrid(string raster, string width, string height)
        => ResizePreservingGrid(raster, (width, height), DefaultResamplingAlgorithm);

    internal static string ResizePreservingGrid(
        string raster,
        (string Width, string Height) dimensions,
        string algorithm = DefaultResamplingAlgorithm)
    {
        var validated = ValidateResamplingAlgorithm(algorithm);
        var (width, height) = dimensions;
        return $"""
            (SELECT CASE WHEN ST_Width(resize_source.rast) = {width} AND ST_Height(resize_source.rast) = {height}
                THEN resize_source.rast
                ELSE ST_SetSRID(ST_SetUpperLeft(ST_SetSkew(ST_SetScale(
                    ST_Resize(ST_SetSRID(ST_SetGeoReference(resize_source.rast,
                        0, ST_Height(resize_source.rast), 1, -1, 0, 0), 0), {width}, {height}, '{validated}'),
                    ST_ScaleX(resize_source.rast) * ST_Width(resize_source.rast) / {width},
                    ST_ScaleY(resize_source.rast) * ST_Height(resize_source.rast) / {height}),
                    ST_SkewX(resize_source.rast) * ST_Height(resize_source.rast) / {height},
                    ST_SkewY(resize_source.rast) * ST_Width(resize_source.rast) / {width}),
                    ST_UpperLeftX(resize_source.rast), ST_UpperLeftY(resize_source.rast)),
                    ST_SRID(resize_source.rast)) END
             FROM (SELECT {raster} AS rast OFFSET 0) resize_source)
            """;
    }
}
