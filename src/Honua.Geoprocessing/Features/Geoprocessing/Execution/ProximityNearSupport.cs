// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared nearest-feature computation for the managed proximity tools
/// <c>proximity.near</c> and <c>proximity.near-table</c> (#2139). Computes, for an
/// input geometry, the planar (CRS-unit) distance to the closest feature in a near
/// layer and that feature's identifier — the <c>NEAR_DIST</c>/<c>NEAR_FID</c>
/// semantics of Esri's <c>Near</c>/<c>GenerateNearTable</c>. Distances are planar
/// (<see cref="NetTopologySuite.Geometries.Geometry.Distance"/>); geodesic
/// conversion is not performed, matching the rest of the managed tool packs.
/// </summary>
internal static class ProximityNearSupport
{
    /// <summary>Sentinel <c>NEAR_FID</c>/<c>NEAR_DIST</c> when no neighbour is within range.</summary>
    public const long NoNeighbourFid = -1;

    /// <summary>The resolved nearest-feature result for a single input feature.</summary>
    public readonly record struct NearResult(long NearFid, double NearDist, bool Found);

    /// <summary>
    /// Materializes the near layer's identifier for every feature: the value of
    /// <paramref name="idField"/> when supplied and present, otherwise the 0-based
    /// ordinal.
    /// </summary>
    public static long[] ResolveIds(FeatureCollection layer, string? idField)
    {
        var ids = new long[layer.Count];
        for (var i = 0; i < layer.Count; i++)
        {
            ids[i] = ResolveId(layer[i], idField, i);
        }

        return ids;
    }

    /// <summary>Resolves a single feature's identifier from an id field or ordinal.</summary>
    public static long ResolveId(IFeature feature, string? idField, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(idField)
            && feature.Attributes is not null
            && feature.Attributes.Exists(idField))
        {
            var raw = feature.Attributes.GetOptionalValue(idField);
            switch (raw)
            {
                case null:
                    break;
                case long l:
                    return l;
                case int i:
                    return i;
                case short s:
                    return s;
                default:
                    if (long.TryParse(
                        Convert.ToString(raw, CultureInfo.InvariantCulture),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                    {
                        return parsed;
                    }

                    break;
            }
        }

        return ordinal;
    }

    /// <summary>
    /// Finds the nearest near-feature to <paramref name="inputGeometry"/> within an
    /// optional <paramref name="searchRadius"/> (CRS units; non-positive means
    /// unbounded). Returns a sentinel result when nothing is in range.
    /// </summary>
    public static NearResult FindNearest(
        NetTopologySuite.Geometries.Geometry? inputGeometry,
        FeatureCollection nearLayer,
        long[] nearIds,
        double searchRadius)
    {
        if (inputGeometry is null || inputGeometry.IsEmpty)
        {
            return new NearResult(NoNeighbourFid, NoNeighbourFid, false);
        }

        var bestDist = double.PositiveInfinity;
        var bestFid = NoNeighbourFid;

        for (var i = 0; i < nearLayer.Count; i++)
        {
            var candidate = nearLayer[i].Geometry;
            if (candidate is null || candidate.IsEmpty)
            {
                continue;
            }

            var distance = inputGeometry.Distance(candidate);
            if (distance < bestDist)
            {
                bestDist = distance;
                bestFid = nearIds[i];
            }
        }

        if (double.IsInfinity(bestDist) || (searchRadius > 0d && bestDist > searchRadius))
        {
            return new NearResult(NoNeighbourFid, NoNeighbourFid, false);
        }

        return new NearResult(bestFid, bestDist, true);
    }

    /// <summary>Parses the optional <c>searchRadius</c> input (non-positive when absent/blank).</summary>
    public static double ReadSearchRadius(StepInputReader inputs)
    {
        if (!inputs.TryGet("searchRadius", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return 0d;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius)
            || double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0d)
        {
            throw new TransformInputException(
                $"'searchRadius' must be a non-negative finite number, got '{raw}'");
        }

        return radius;
    }
}
