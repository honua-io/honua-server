// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.TileCachePackage.Services;

/// <summary>One level-of-detail sample from root.json or conf.xml.</summary>
internal readonly record struct TileCacheLodSample(int Level, double? Resolution, double? Scale);

/// <summary>
/// Accepts a package tiling scheme only when its WKID, origin, and level resolutions
/// match WebMercatorQuad or WorldCRS84Quad. Anything else is rejected.
/// </summary>
internal static class TileCachePackageScheme
{
    internal const string WebMercatorQuad = "WebMercatorQuad";
    internal const string WorldCrs84Quad = "WorldCRS84Quad";
    internal const string MissingWkidWebMercatorAssumption =
        "WKID was missing or 0; tiling scheme matched WebMercatorQuad.";
    internal const string MissingWkidWorldCrs84Assumption =
        "WKID was missing or 0; tiling scheme matched WorldCRS84Quad.";

    internal const double WebMercatorHalfWorldMeters = 20037508.342789244;
    private const double MercatorOriginToleranceMeters = 1.0;
    private const double GeographicOriginToleranceDegrees = 1e-6;
    private const double ResolutionRelativeTolerance = 1e-4;
    private const double ResolutionAbsoluteTolerance = 1e-6;
    private const double MetersPerInch = 0.0254;
    private const double ScreenDotsPerInch = 96.0;

    internal readonly record struct Match(string Identifier, string? Assumption);

    internal static Match Resolve(
        int wkid,
        double? originX,
        double? originY,
        int tileSize,
        IReadOnlyList<TileCacheLodSample> lods)
    {
        if (tileSize <= 0 || originX is null || originY is null || lods is null || lods.Count == 0)
        {
            throw SchemeRejected(wkid, "it is missing an origin or level resolutions");
        }

        var mercatorOrigin = OriginsMatch(
            originX.Value,
            originY.Value,
            -WebMercatorHalfWorldMeters,
            WebMercatorHalfWorldMeters,
            MercatorOriginToleranceMeters);
        var geographicOrigin = OriginsMatch(
            originX.Value,
            originY.Value,
            -180d,
            90d,
            GeographicOriginToleranceDegrees);

        var mercatorResolution = (2d * WebMercatorHalfWorldMeters) / tileSize;
        var geographicResolution = 180d / tileSize;
        var mercator = mercatorOrigin && LevelsMatch(lods, mercatorResolution, allowScale: true);
        var geographic = geographicOrigin && LevelsMatch(lods, geographicResolution, allowScale: false);

        if (IsWebMercator(wkid))
        {
            if (!mercator)
            {
                throw SchemeRejected(wkid, "it does not match the WebMercatorQuad tiling scheme");
            }

            return new Match(WebMercatorQuad, null);
        }

        if (wkid == 4326)
        {
            if (!geographic)
            {
                throw SchemeRejected(wkid, "it does not match the WorldCRS84Quad tiling scheme");
            }

            return new Match(WorldCrs84Quad, null);
        }

        if (wkid == 0)
        {
            if (mercator)
            {
                return new Match(WebMercatorQuad, MissingWkidWebMercatorAssumption);
            }

            if (geographic)
            {
                return new Match(WorldCrs84Quad, MissingWkidWorldCrs84Assumption);
            }
        }

        throw SchemeRejected(wkid, "it does not match WebMercatorQuad or WorldCRS84Quad");
    }

    private static bool IsWebMercator(int wkid) => wkid is 3857 or 102100 or 102113 or 900913;

    private static NotSupportedException SchemeRejected(int wkid, string reason)
        => new($"Tile-cache package spatial reference {wkid} was rejected because {reason}.");

    private static bool OriginsMatch(double x, double y, double expectedX, double expectedY, double tolerance)
        => Math.Abs(x - expectedX) <= tolerance && Math.Abs(y - expectedY) <= tolerance;

    private static bool LevelsMatch(
        IReadOnlyList<TileCacheLodSample> lods,
        double resolutionAtLevelZero,
        bool allowScale)
    {
        foreach (var lod in lods)
        {
            if (lod.Level < 0)
            {
                return false;
            }

            if (!TryResolveResolution(lod, allowScale, out var actual))
            {
                return false;
            }

            var expected = resolutionAtLevelZero / Math.Pow(2d, lod.Level);
            var difference = Math.Abs(actual - expected);
            var tolerance = ResolutionAbsoluteTolerance + (ResolutionRelativeTolerance * Math.Abs(expected));
            if (difference > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveResolution(TileCacheLodSample lod, bool allowScale, out double resolution)
    {
        if (lod.Resolution is > 0d && !double.IsInfinity(lod.Resolution.Value))
        {
            resolution = lod.Resolution.Value;
            return true;
        }

        if (allowScale && lod.Scale is > 0d && !double.IsInfinity(lod.Scale.Value))
        {
            resolution = lod.Scale.Value * MetersPerInch / ScreenDotsPerInch;
            return true;
        }

        resolution = 0d;
        return false;
    }
}
