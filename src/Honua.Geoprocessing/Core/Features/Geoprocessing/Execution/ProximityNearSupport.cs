// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

internal static class ProximityNearSupport
{
    public const long NoNeighbourFid = -1;

    public readonly record struct NearResult(long NearFid, double NearDist, bool Found);

    public static long[] ResolveIds(FeatureCollection layer, string? idField)
    {
        var ids = new long[layer.Count];
        for (var i = 0; i < layer.Count; i++)
        {
            ids[i] = ResolveId(layer[i], idField, i);
        }

        return ids;
    }

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
