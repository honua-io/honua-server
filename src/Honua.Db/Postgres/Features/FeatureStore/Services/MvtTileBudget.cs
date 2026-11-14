// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Tiles;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

// A bounded tile encoding retry. Retries remove optional attributes before increasing
// topology-preserving simplification; they never impose an adaptive feature limit.
internal readonly record struct MvtTileBudgetStep(int Index, double SimplifyScale, bool OmitAttributes)
{
    public static MvtTileBudgetStep Full { get; } = new(0, 1d, false);
    public bool IsFull => Index == 0;

    public MvtTileBudgetStep? Next() => Index switch
    {
        0 => new MvtTileBudgetStep(1, 0d, true),
        1 => new MvtTileBudgetStep(2, 4d, true),
        2 => new MvtTileBudgetStep(3, 8d, true),
        3 => new MvtTileBudgetStep(4, 16d, true),
        4 => new MvtTileBudgetStep(5, 32d, true),
        5 => new MvtTileBudgetStep(6, 64d, true),
        _ => null
    };
}

internal readonly record struct MvtGeometryEncoding(string Envelope, string Extent, string Buffer);

// SQL fragments for a budget pass. The full pass reproduces the existing simplification
// rule so a tile that already fits retains its geometry and attributes.
internal static class MvtTileBudget
{
    // Map-unit simplification distance for a pass. Full fidelity, and a pass with no
    // simplify scale, return 0 and the caller keeps the geometry it already had.
    // useWebMercatorZoomFloor is true only for the built-in Web Mercator
    // pyramid, where the zoom table is a lower bound. Operator-defined gridsets pass false
    // so the tolerance is that tile's own resolution.
    internal static double Tolerance(
        int zoom,
        double tileSpan,
        int tileExtent,
        bool geographic,
        MvtTileBudgetStep step,
        bool useWebMercatorZoomFloor = true)
    {
        if (step.IsFull || tileSpan <= 0 || step.SimplifyScale <= 0)
        {
            return 0;
        }

        var tileUnit = tileExtent > 0 ? tileSpan / tileExtent : tileSpan;
        var zoomTolerance = geographic || !useWebMercatorZoomFloor
            ? 0d
            : TileMath.GetSimplificationTolerance(zoom);
        return Math.Max(zoomTolerance, tileUnit) * step.SimplifyScale;
    }

    // Historical zoom simplification, or topology-preserving retry simplification.
    internal static string ApplySimplification(
        string geometrySql,
        MvtTileBudgetStep step,
        bool geographic,
        int zoom,
        double tileSpan,
        int tileExtent,
        int simplifyZoom,
        Func<double, string> parameter)
    {
        if (step.IsFull)
        {
            if (geographic || zoom > simplifyZoom)
            {
                return geometrySql;
            }

            var tolerance = TileMath.GetSimplificationTolerance(zoom);
            if (tolerance <= 0)
            {
                return geometrySql;
            }

            return $"ST_SimplifyPreserveTopology({geometrySql}, {parameter(tolerance)})";
        }

        return Generalize(geometrySql, Tolerance(zoom, tileSpan, tileExtent, geographic, step), parameter);
    }

    // Topology-preserving simplification without snapping away small geometries.
    internal static string Generalize(string geometrySql, double tolerance, Func<double, string> parameter)
        => tolerance <= 0
            ? geometrySql
            : $"ST_SimplifyPreserveTopology({geometrySql}, {parameter(tolerance)})";

    // Keep the original visibility set: simplification must neither add nor remove a feature.
    internal static string EncodeGeometry(string geometrySql, string originalSql, MvtGeometryEncoding encoding)
    {
        var encoded = $"ST_AsMVTGeom({geometrySql}, {encoding.Envelope}, {encoding.Extent}, {encoding.Buffer})";
        if (geometrySql == originalSql)
        {
            return encoded;
        }

        var original = $"ST_AsMVTGeom({originalSql}, {encoding.Envelope}, {encoding.Extent}, {encoding.Buffer})";
        return $"CASE WHEN {original} IS NULL THEN NULL ELSE COALESCE({encoded}, {original}) END";
    }
}

// Re-encodes a vector tile with progressively stricter passes until it fits the byte budget.
// A pass that already fits is returned immediately. An empty retry is not a fit.
// Exhausting the bounded ladder refuses the tile instead of returning a feature subset.
// A genuinely empty first pass still returns no tile.
internal static class MvtTileBudgetFitter
{
    internal static async Task<byte[]?> ReadAsync<TQuery>(
        long maxTileSize,
        Func<MvtTileBudgetStep, TQuery> build,
        Func<TQuery, long, CancellationToken, Task<byte[]?>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(execute);

        var step = MvtTileBudgetStep.Full;
        TileSizeLimitExceededException? refusal = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? tile;
            try
            {
                tile = await execute(build(step), maxTileSize, cancellationToken).ConfigureAwait(false);
            }
            catch (TileSizeLimitExceededException ex) when (step.Next() is { } next)
            {
                refusal = ex;
                step = next;
                continue;
            }

            if (tile is { Length: > 0 } || step.IsFull)
            {
                return tile;
            }

            if (step.Next() is { } nextStep)
            {
                step = nextStep;
                continue;
            }

            // The first pass was over budget, so an empty retry is a refusal, not an empty tile.
            throw refusal ?? new TileSizeLimitExceededException(-1, maxTileSize);
        }
    }
}
