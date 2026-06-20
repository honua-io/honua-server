// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Shared overview/pyramid-level selection scoring. Both the COG tile resolver
/// (<c>CogTileResolver.FindBestOverviewLevel</c>) and the PostGIS persisted-pyramid tile read
/// path pick the reduced-resolution level whose ground sample distance best matches the
/// requested tile's ground resolution, so the score lives here once instead of being duplicated
/// per protocol (#1836).
/// </summary>
public static class OverviewLevelSelector
{
    /// <summary>
    /// Scores how well an overview level matches a requested tile, lower is better. The score
    /// is the absolute difference, summed across both axes, between the number of overview pixels
    /// that fall inside the requested tile envelope and the tile's pixel dimensions. A perfect
    /// match (one overview pixel per tile pixel) scores 0.
    /// </summary>
    /// <param name="tileSpanX">Ground span of the requested tile along X (CRS units).</param>
    /// <param name="tileSpanY">Ground span of the requested tile along Y (CRS units).</param>
    /// <param name="tilePixelWidth">Tile width in pixels (e.g. 256).</param>
    /// <param name="tilePixelHeight">Tile height in pixels (e.g. 256).</param>
    /// <param name="overviewGroundResolutionX">Overview ground sample distance along X (CRS units per pixel).</param>
    /// <param name="overviewGroundResolutionY">Overview ground sample distance along Y (CRS units per pixel).</param>
    /// <returns>The non-negative match score, or <see cref="double.MaxValue"/> for invalid inputs.</returns>
    public static double Score(
        double tileSpanX,
        double tileSpanY,
        int tilePixelWidth,
        int tilePixelHeight,
        double overviewGroundResolutionX,
        double overviewGroundResolutionY)
    {
        if (tileSpanX <= 0 || tileSpanY <= 0 ||
            tilePixelWidth <= 0 || tilePixelHeight <= 0 ||
            overviewGroundResolutionX <= 0 || overviewGroundResolutionY <= 0 ||
            !double.IsFinite(overviewGroundResolutionX) || !double.IsFinite(overviewGroundResolutionY))
        {
            return double.MaxValue;
        }

        var widthScore = Math.Abs((tileSpanX / overviewGroundResolutionX) - tilePixelWidth);
        var heightScore = Math.Abs((tileSpanY / overviewGroundResolutionY) - tilePixelHeight);
        return widthScore + heightScore;
    }

    /// <summary>
    /// Selects the best-scoring candidate index from a list of overview ground resolutions for the
    /// requested tile geometry. Returns <c>-1</c> when there are no positive-resolution candidates.
    /// </summary>
    /// <param name="tileSpanX">Ground span of the requested tile along X (CRS units).</param>
    /// <param name="tileSpanY">Ground span of the requested tile along Y (CRS units).</param>
    /// <param name="tilePixelWidth">Tile width in pixels.</param>
    /// <param name="tilePixelHeight">Tile height in pixels.</param>
    /// <param name="candidateResolutions">
    /// Per-candidate (groundResolutionX, groundResolutionY) ground sample distances.
    /// </param>
    public static int SelectBestIndex(
        double tileSpanX,
        double tileSpanY,
        int tilePixelWidth,
        int tilePixelHeight,
        IReadOnlyList<(double ResolutionX, double ResolutionY)> candidateResolutions)
    {
        ArgumentNullException.ThrowIfNull(candidateResolutions);

        var bestIndex = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < candidateResolutions.Count; i++)
        {
            var (resX, resY) = candidateResolutions[i];
            var score = Score(tileSpanX, tileSpanY, tilePixelWidth, tilePixelHeight, resX, resY);
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = i;
                if (score <= 1e-6)
                {
                    break;
                }
            }
        }

        return bestIndex;
    }
}
