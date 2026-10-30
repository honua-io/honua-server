// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Exceptions;
using Honua.Core.Features.Tiles;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// One deterministic pass of the low-zoom tile budget ladder. The first pass is the
/// historical query. Later passes snap and simplify more aggressively, drop sub-pixel
/// lines and polygons, then drop attributes and features until the encoded tile fits
/// <c>Limits:Tiles:MaxTileSize</c>.
/// </summary>
internal readonly record struct MvtTileBudgetStep(
    int Index,
    double SimplifyScale,
    double MinTileUnits,
    int? FeatureCap,
    bool OmitAttributes)
{
    /// <summary>The historical query: no extra snap, pixel filter, or attribute drop.</summary>
    public static MvtTileBudgetStep Full { get; } = new(0, 1d, 0d, null, false);

    /// <summary>True for the query that existing callers already depend on.</summary>
    public bool IsFull => Index == 0;

    /// <summary>The next stricter pass, or null when a single feature still cannot fit.</summary>
    public MvtTileBudgetStep? Next() => Index switch
    {
        0 => new MvtTileBudgetStep(1, 4d, 1d, null, false),
        1 => new MvtTileBudgetStep(2, 8d, 2d, 4_000, true),
        2 => new MvtTileBudgetStep(3, 16d, 4d, 800, true),
        3 => new MvtTileBudgetStep(4, 16d, 0d, 200, true),
        4 => new MvtTileBudgetStep(5, 32d, 0d, 40, true),
        5 => new MvtTileBudgetStep(6, 64d, 0d, 8, true),
        6 => new MvtTileBudgetStep(7, 64d, 0d, 1, true),
        // No snap and no pixel filter. Earlier passes can collapse every line and
        // polygon; this pass keeps one original feature when attributes were the cost.
        7 => new MvtTileBudgetStep(8, 0d, 0d, 1, true),
        _ => null
    };
}

/// <summary>
/// SQL fragments for a budget pass. The full pass reproduces the existing simplification
/// rule so a tile that already fits stays byte-identical.
/// </summary>
internal static class MvtTileBudget
{
    /// <summary>
    /// Map-unit simplification distance for a pass. Full fidelity, and a pass with no
    /// simplify scale, return 0 and the caller keeps the geometry it already had.
    /// <paramref name="useWebMercatorZoomFloor"/> is true only for the built-in Web Mercator
    /// pyramid, where the zoom table is a lower bound. Operator-defined gridsets pass false
    /// so the tolerance is that tile's own resolution.
    /// </summary>
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

    /// <summary>Map units of one tile pixel times the pass's minimum visible size. 0 disables the filter.</summary>
    internal static double MinMapUnits(double tileSpan, int tileExtent, MvtTileBudgetStep step)
    {
        if (step.IsFull || step.MinTileUnits <= 0 || tileSpan <= 0 || tileExtent <= 0)
        {
            return 0;
        }

        return step.MinTileUnits * (tileSpan / tileExtent);
    }

    /// <summary>
    /// Feature cap for this pass. Null means the query keeps its historical limit (including
    /// no limit). A thinning pass never exceeds the caller's configured cap.
    /// </summary>
    internal static int? FeatureLimit(int configuredMax, MvtTileBudgetStep step)
    {
        if (step.IsFull || step.FeatureCap is not int cap || cap <= 0)
        {
            return configuredMax > 0 ? configuredMax : null;
        }

        return configuredMax > 0 ? Math.Min(configuredMax, cap) : cap;
    }

    /// <summary>
    /// Zoom simplification for the full pass, or snap-and-simplify for a thinning pass.
    /// Points are not snapped: moving them by a grid cell can push them out of the tile.
    /// </summary>
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

    /// <summary>
    /// Snaps lines and polygons to the tolerance grid, repairs the snap, then simplifies
    /// without opening a self-intersection. A zero tolerance leaves the expression unchanged.
    /// </summary>
    internal static string Generalize(string geometrySql, double tolerance, Func<double, string> parameter)
    {
        if (tolerance <= 0)
        {
            return geometrySql;
        }

        var token = parameter(tolerance);
        return string.Create(CultureInfo.InvariantCulture, $"""
            CASE WHEN ST_Dimension({geometrySql}) = 0 THEN {geometrySql}
            ELSE ST_SimplifyPreserveTopology(
                ST_CollectionExtract(
                    ST_MakeValid(ST_SnapToGrid({geometrySql}, {token})),
                    ST_Dimension({geometrySql})::integer + 1),
                {token})
            END
            """);
    }

    /// <summary>
    /// Drops lines and polygons smaller than <paramref name="minMapUnits"/>. Points stay;
    /// they have no extent to measure. Null when the pass does not filter.
    /// </summary>
    internal static string? MinSizePredicate(string geometrySql, double minMapUnits, Func<double, string> parameter)
    {
        if (minMapUnits <= 0)
        {
            return null;
        }

        var token = parameter(minMapUnits);
        return $"(ST_Dimension({geometrySql}) = 0 OR GREATEST(ST_XMax({geometrySql}) - ST_XMin({geometrySql}), ST_YMax({geometrySql}) - ST_YMin({geometrySql})) >= {token})";
    }
}

/// <summary>
/// Re-encodes a vector tile with progressively stricter passes until it fits the byte budget.
/// A pass that already fits is returned immediately. An empty thinning pass is not a fit:
/// the next pass samples features the pixel filter removed. The last pass keeps one feature
/// without snapping. An empty last pass, or a last pass that is still over budget, refuses.
/// A genuinely empty first pass still returns no tile.
/// </summary>
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

            // The first pass was over budget, so this tile has features. Snapping can
            // remove every one of them; that is a refusal, not an empty tile.
            throw refusal ?? new TileSizeLimitExceededException(-1, maxTileSize);
        }
    }
}
