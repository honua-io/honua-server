// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public static class OverviewLevelSelector
{
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
